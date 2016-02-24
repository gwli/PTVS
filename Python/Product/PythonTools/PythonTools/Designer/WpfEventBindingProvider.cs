// Python Tools for Visual Studio
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
using System.Linq;
using System.Text;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Parsing.Ast;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.Windows.Design.Host;

namespace Microsoft.PythonTools.Designer {
    class WpfEventBindingProvider : EventBindingProvider {
        private Project.PythonFileNode _pythonFileNode;

        public WpfEventBindingProvider(Project.PythonFileNode pythonFileNode) {
            _pythonFileNode = pythonFileNode;
        }

        public override bool AddEventHandler(EventDescription eventDescription, string objectName, string methodName) {
            // we return false here which causes the event handler to always be wired up via XAML instead of via code.
            return false;
        }

        public override bool AllowClassNameForMethodName() {
            return true;
        }

        public override void AppendStatements(EventDescription eventDescription, string methodName, string statements, int relativePosition) {
            throw new NotImplementedException();
        }

        public override string CodeProviderLanguage {
            get { return "Python"; }
        }

        public override bool CreateMethod(EventDescription eventDescription, string methodName, string initialStatements) {
            // build the new method handler
            var view = _pythonFileNode.GetTextView();
            var textBuffer = _pythonFileNode.GetTextBuffer();
            PythonAst ast;
            var classDef = GetClassForEvents(out ast);
            if (classDef != null) {
                int end = classDef.Body.EndIndex;
                
                // insert after the newline at the end of the last statement of the class def
                if (textBuffer.CurrentSnapshot[end] == '\r') {
                    if (end + 1 < textBuffer.CurrentSnapshot.Length &&
                        textBuffer.CurrentSnapshot[end + 1] == '\n') {
                        end += 2;
                    } else {
                        end++;
                    }
                } else if (textBuffer.CurrentSnapshot[end] == '\n') {
                    end++;
                }

                using (var edit = textBuffer.CreateEdit()) {
                    var text = BuildMethod(
                        eventDescription,
                        methodName,
                        new string(' ', classDef.Body.GetStart(ast).Column - 1),
                        view.Options.IsConvertTabsToSpacesEnabled() ?
                            view.Options.GetIndentSize() :
                            -1);

                    edit.Insert(end, text);
                    edit.Apply();
                    return true;
                }
            }


            return false;
        }

        private ClassDefinition GetClassForEvents() {
            PythonAst ast;
            return GetClassForEvents(out ast);
        }

        private ClassDefinition GetClassForEvents(out PythonAst ast) {
            ast = null;
#if FALSE
            var analysis = _pythonFileNode.GetProjectEntry() as IPythonProjectEntry;

            if (analysis != null) {
                // TODO: Wait for up to date analysis
                ast = analysis.WaitForCurrentTree();
                var suiteStmt = ast.Body as SuiteStatement;
                foreach (var stmt in suiteStmt.Statements) {
                    var classDef = stmt as ClassDefinition;
                    // TODO: Make sure this is the right class
                    if (classDef != null) {
                        return classDef;
                    }
                }
            }
#endif
            return null;
        }

        private static string BuildMethod(EventDescription eventDescription, string methodName, string indentation, int tabSize) {
            StringBuilder text = new StringBuilder();
            text.AppendLine(indentation);
            text.Append(indentation);
            text.Append("def ");
            text.Append(methodName);
            text.Append('(');
            text.Append("self");
            foreach (var param in eventDescription.Parameters) {
                text.Append(", ");
                text.Append(param.Name);
            }
            text.AppendLine("):");
            if (tabSize < 0) {
                text.Append(indentation);
                text.Append("\tpass");
            } else {
                text.Append(indentation);
                text.Append(' ', tabSize);
                text.Append("pass");
            }
            text.AppendLine();

            return text.ToString();
        }

        public override string CreateUniqueMethodName(string objectName, EventDescription eventDescription) {
            var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_{1}", objectName, eventDescription.Name);
            int count = 0;
            while (IsExistingMethodName(eventDescription, name)) {
                name = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_{1}{2}", objectName, eventDescription.Name, ++count);
            }
            return name;
        }

        public override IEnumerable<string> GetCompatibleMethods(EventDescription eventDescription) {
            var classDef = GetClassForEvents();
            SuiteStatement suite = classDef.Body as SuiteStatement;

            if (suite != null) {
                int requiredParamCount = eventDescription.Parameters.Count() + 1;
                foreach (var methodCandidate in suite.Statements) {
                    FunctionDefinition funcDef = methodCandidate as FunctionDefinition;
                    if (funcDef != null) {
                        // Given that event handlers can be given any arbitrary 
                        // name, it is important to not rely on the default naming 
                        // to detect compatible methods.  Instead we look at the 
                        // event parameters. We don't have param types in Python, 
                        // so really the only thing that can be done is look at 
                        // the method parameter count, which should be one more than
                        // the event parameter count (to account for the self param).
                        if (funcDef.Parameters.Count == requiredParamCount) {
                            yield return funcDef.Name;
                        }
                    }
                }
            }
        }

        public override IEnumerable<string> GetMethodHandlers(EventDescription eventDescription, string objectName) {
            return new string[0];
        }

        public override bool IsExistingMethodName(EventDescription eventDescription, string methodName) {
            return FindMethod(methodName) != null;
        }

        private FunctionDefinition FindMethod(string methodName) {
            var classDef = GetClassForEvents();
            SuiteStatement suite = classDef.Body as SuiteStatement;

            if (suite != null) {
                foreach (var methodCandidate in suite.Statements) {
                    FunctionDefinition funcDef = methodCandidate as FunctionDefinition;
                    if (funcDef != null) {
                        if (funcDef.Name == methodName) {
                            return funcDef;
                        }
                    }
                }
            }

            return null;
        }

        public override bool RemoveEventHandler(EventDescription eventDescription, string objectName, string methodName) {
            var method = FindMethod(methodName);
            if (method != null) {
                var view = _pythonFileNode.GetTextView();
                var textBuffer = _pythonFileNode.GetTextBuffer();

                // appending a method adds 2 extra newlines, we want to remove those if those are still
                // present so that adding a handler and then removing it leaves the buffer unchanged.

                using (var edit = textBuffer.CreateEdit()) {
                    int start = method.StartIndex - 1;

                    // eat the newline we insert before the method
                    while (start >= 0) {
                        var curChar = edit.Snapshot[start];
                        if (!Char.IsWhiteSpace(curChar)) {
                            break;
                        } else if (curChar == ' ' || curChar == '\t') {
                            start--;
                            continue;
                        } else if (curChar == '\n') {
                            if (start != 0) {
                                if (edit.Snapshot[start - 1] == '\r') {
                                    start--;
                                }
                            }
                            start--;
                            break;
                        } else if (curChar == '\r') {
                            start--;
                            break;
                        }

                        start--;
                    }

                    
                    // eat the newline we insert at the end of the method
                    int end = method.EndIndex;                    
                    while (end < edit.Snapshot.Length) {
                        if (edit.Snapshot[end] == '\n') {
                            end++;
                            break;
                        } else if (edit.Snapshot[end] == '\r') {
                            if (end < edit.Snapshot.Length - 1 && edit.Snapshot[end + 1] == '\n') {
                                end += 2;
                            } else {
                                end++;
                            }
                            break;
                        } else if (edit.Snapshot[end] == ' ' || edit.Snapshot[end] == '\t') {
                            end++;
                            continue;
                        } else {
                            break;
                        }
                    }

                    // delete the method and the extra whitespace that we just calculated.
                    edit.Delete(Span.FromBounds(start + 1, end));
                    edit.Apply();
                }

                return true;
            }
            return false;
        }

        public override bool RemoveHandlesForName(string elementName) {
            throw new NotImplementedException();
        }

        public override bool RemoveMethod(EventDescription eventDescription, string methodName) {
            throw new NotImplementedException();
        }

        public override void SetClassName(string className) {
        }

        public override bool ShowMethod(EventDescription eventDescription, string methodName) {
            var method = FindMethod(methodName);
            if (method != null) {
                var view = _pythonFileNode.GetTextView();
                view.Caret.MoveTo(new VisualStudio.Text.SnapshotPoint(view.TextSnapshot, method.StartIndex));
                view.Caret.EnsureVisible();
                return true;
            }

            return false;
        }

        public override void ValidateMethodName(EventDescription eventDescription, string methodName) {
        }
    }
}
