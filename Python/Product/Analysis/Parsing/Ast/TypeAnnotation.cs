﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Microsoft.PythonTools.Parsing.Ast {
    class TypeAnnotation {
        public TypeAnnotation(PythonLanguageVersion version, Expression expr) {
            LanguageVersion = version;
            Expression = expr ?? throw new ArgumentNullException(nameof(expr));
        }

        public static TypeAnnotation FromType<T>(TypeAnnotationConverter<T> converter, T type) where T : class {
            throw new NotImplementedException();
        }

        public PythonLanguageVersion LanguageVersion { get; }
        public Expression Expression { get; }

        private Expression ParseSubExpression(string expr) {
            using (var parser = Parser.CreateParser(new StringReader(expr), LanguageVersion)) {
                return Statement.GetExpression(parser.ParseTopExpression()?.Body);
            }
        }

        /// <summary>
        /// Converts this type annotation 
        /// </summary>
        public T GetValue<T>(TypeAnnotationConverter<T> converter) where T : class {
            var walker = new Walker(ParseSubExpression);
            Expression.Walk(walker);
            return walker.GetResult(converter);
        }

        internal IEnumerable<string> GetTransformSteps() {
            var walker = new Walker(ParseSubExpression);
            Expression.Walk(walker);
            return walker._ops.Select(o => o.ToString());
        }

        private class Walker : PythonWalker {
            private readonly Func<string, Expression> _parse;
            internal readonly List<Op> _ops;

            public Walker(Func<string, Expression> parse) {
                _parse = parse;
                _ops = new List<Op>();
            }

            public T GetResult<T>(TypeAnnotationConverter<T> converter) where T : class {
                var stack = new Stack<KeyValuePair<string, T>>();
                foreach (var op in _ops) {
                    if (!op.Apply(converter, stack)) {
                        return default(T);
                    }
                }

                if (!stack.Any()) {
                    return default(T);
                }
                Debug.Assert(stack.Count == 1);
                return converter.Finalize(stack.Pop().Value);
            }

            public override bool Walk(ConstantExpression node) {
                if (node.Value is string s) {
                    _parse(s)?.Walk(this);
                } else if (node.Value is AsciiString a) {
                    _parse(a.String)?.Walk(this);
                }
                return false;
            }

            public override bool Walk(NameExpression node) {
                _ops.Add(new NameOp { Name = node.Name });
                return false;
            }

            public override bool Walk(MemberExpression node) {
                if (base.Walk(node)) {
                    node.Target?.Walk(this);
                    _ops.Add(new MemberOp { Member = node.Name });
                }
                return false;
            }

            public override bool Walk(TupleExpression node) {
                _ops.Add(new StartUnionOp());
                return base.Walk(node);
            }

            public override void PostWalk(TupleExpression node) {
                _ops.Add(new EndUnionOp());
                base.PostWalk(node);
            }

            public override void PostWalk(IndexExpression node) {
                _ops.Add(new MakeGenericOp());
                base.PostWalk(node);
            }

            internal abstract class Op {
                public abstract bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) where T : class;
                public override string ToString() => GetType().Name;
            }

            class NameOp : Op {
                public string Name { get; set; }

                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    var t = converter.LookupName(Name);
                    if (t == null) {
                        return false;
                    }
                    stack.Push(new KeyValuePair<string, T>(null, t));
                    return true;
                }

                public override string ToString() => $"{GetType().Name}:{Name}";
            }

            class MemberOp : Op {
                public string Member { get; set; }

                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    if (!stack.Any()) {
                        return false;
                    }
                    var t = stack.Pop();
                    if (t.Key != null) {
                        return false;
                    }
                    t = new KeyValuePair<string, T>(null, converter.GetTypeMember(t.Value, Member));
                    if (t.Value == null) {
                        return false;
                    }
                    stack.Push(t);
                    return true;
                }

                public override string ToString() => $"{GetType().Name}:{Member}";
            }

            class OptionalOp : Op {
                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    if (!stack.Any()) {
                        return false;
                    }
                    var t = stack.Pop();
                    if (t.Key != null) {
                        return false;
                    }
                    t = new KeyValuePair<string, T>(null, converter.MakeOptional(t.Value));
                    if (t.Value == null) {
                        return false;
                    }
                    stack.Push(t);
                    return true;
                }
            }

            class StartUnionOp : Op {
                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    stack.Push(new KeyValuePair<string, T>(nameof(StartUnionOp), null));
                    return true;
                }
            }

            class EndUnionOp : Op {
                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    var items = new List<T>();
                    if (!stack.Any()) {
                        return false;
                    }
                    var t = stack.Pop();
                    while (t.Key != nameof(StartUnionOp)) {
                        items.Add(t.Value);
                        if (!stack.Any()) {
                            return false;
                        }
                        t = stack.Pop();
                    }
                    items.Reverse();
                    t = new KeyValuePair<string, T>(null, converter.MakeUnion(items));
                    if (t.Value == null) {
                        return false;
                    }
                    stack.Push(t);
                    return true;
                }
            }

            class MakeGenericOp : Op {
                public override bool Apply<T>(TypeAnnotationConverter<T> converter, Stack<KeyValuePair<string, T>> stack) {
                    if (stack.Count < 2) {
                        return false;
                    }
                    var args = stack.Pop();
                    if (args.Key != null) {
                        return false;
                    }
                    var baseType = stack.Pop();
                    if (baseType.Key != null) {
                        return false;
                    }
                    var t = converter.MakeGeneric(baseType.Value, converter.GetUnionTypes(args.Value) ?? new[] { args.Value });
                    if (t == null) {
                        return false;
                    }
                    stack.Push(new KeyValuePair<string, T>(null, t));
                    return true;
                }
            }
        }
    }

    public abstract class TypeAnnotationConverter<T> where T : class {
        #region Convert Type Hint to Type

        /// <summary>
        /// Returns the type or module object for the specified name.
        /// </summary>
        public virtual T LookupName(string name) => default(T);
        /// <summary>
        /// Returns a member of the preceding module object.
        /// </summary>
        public virtual T GetTypeMember(T baseType, string member) => default(T);

        /// <summary>
        /// Returns the specialized type object for the base
        /// type and generic types provided.
        /// </summary>
        public virtual T MakeGeneric(T baseType, IReadOnlyList<T> args) => default(T);

        /// <summary>
        /// Returns the type as an optional type.
        /// </summary>
        public virtual T MakeOptional(T type) => default(T);

        /// <summary>
        /// Returns the types as a single union type.
        /// </summary>
        public virtual T MakeUnion(IReadOnlyList<T> types) => default(T);

        /// <summary>
        /// Ensure the final result is a suitable type. Return null
        /// if not.
        /// </summary>
        public virtual T Finalize(T type) => type;

        #endregion


        #region Convert Type to Type Hint

        /// <summary>
        /// Returns the name of the provided type. This should always
        /// be the name of the base type, omitting any generic arguments.
        /// It may include dots to fully qualify the name.
        /// </summary>
        public virtual string GetName(T type) => null;

        /// <summary>
        /// Gets the base type from a generic type. If it is already a
        /// base type, return null.
        /// </summary>
        public virtual T GetBaseType(T genericType) => default(T);
        /// <summary>
        /// Gets the generic types from a generic type. Return null if
        /// there are no generic types.
        /// </summary>
        public virtual IReadOnlyList<T> GetGenericArguments(T genericType) => null;

        /// <summary>
        /// Gets the non-optional type from an optional type. If it is
        /// already a non-optional type, return null.
        /// </summary>
        public virtual T GetNonOptionalType(T optionalType) => default(T);

        /// <summary>
        /// Gets the original types from a type union. If it is not a
        /// union type, return null.
        /// </summary>
        public virtual IReadOnlyList<T> GetUnionTypes(T unionType) => null;
        

        /// <summary>
        /// Returns True if the provided type is not fully defined and
        /// should use a string literal rather than its actual name.
        /// </summary>
        public virtual bool IsForwardReference(T type) => false;

        #endregion
    }
}
