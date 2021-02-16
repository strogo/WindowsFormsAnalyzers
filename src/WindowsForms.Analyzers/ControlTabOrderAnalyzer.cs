﻿#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace WindowsForms.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class ControlTabOrderAnalyzer : DiagnosticAnalyzer
    {
        public static class DiagnosticIds
        {

            public const string NonNumericTabIndexValue = "SWFA0001";

            public const string InconsistentTabIndex = "SWFA0010";

        }

        // You can change these strings in the Resources.resx file. If you do not want your analyzer to be localize-able, you can use regular strings for Title and MessageFormat.
        // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/Localizing%20Analyzers.md for more on localization

        private const string Category = "Accessibility";

        private static readonly DiagnosticDescriptor NonNumericTabIndexValueRule
            = new(DiagnosticIds.NonNumericTabIndexValue,
                  "Ensure numeric controls tab order value",
                  "Control '{0}' has unexpected TabIndex value.",
                  Category,
                  DiagnosticSeverity.Warning,
                  isEnabledByDefault: true,
                  "Avoid manually editing \"InitializeComponent()\" method.");

        private static readonly DiagnosticDescriptor InconsistentTabIndexRule
            = new(DiagnosticIds.InconsistentTabIndex,
                  "Verify correct controls tab order",
                  "Control '{0}' has a different TabIndex value to its order in the parent's control collection.",
                  Category,
                  DiagnosticSeverity.Warning,
                  isEnabledByDefault: true,
                  "Remove TabIndex assignments and re-order controls in the parent's control collection.");

        // Contains the list of fields and local controls that explicitly set TabIndex properties.
        private readonly Dictionary<string, int> _controlsTabIndex = new();
        // Contains the list of fields and local controls in order those are added to parent controls.
        private readonly Dictionary<string, List<string>> _controlsAddIndex = new();


        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(InconsistentTabIndexRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();

            context.RegisterOperationBlockAction(CodeBlockAction);
        }

        private void CodeBlockAction(OperationBlockAnalysisContext context)
        {
            // We only care about "InitializeComponent" method.
            if (context.OwningSymbol is
                    not { Kind: SymbolKind.Method, Name: "InitializeComponent" } and
                    not { Kind: SymbolKind.Field }) // TODO: fields contained in the same class as InitializeComponent
            {
                return;
            }

            foreach (IOperation operationBlock in context.OperationBlocks)
            {
                if (operationBlock is not IBlockOperation blockOperation)
                {
                    continue;
                }

                foreach (IOperation operation in blockOperation.Operations)
                {
                    switch (operation.Kind)
                    {
                        case OperationKind.ExpressionStatement:
                            {
                                var expressionStatementOperation = (IExpressionStatementOperation)operation;

                                // Look for ".Controls.Add"
                                if (expressionStatementOperation.Operation is IOperation invocationOperation &&
                                    invocationOperation.Syntax is InvocationExpressionSyntax expressionSyntax)
                                {
                                    ParseControlAddStatements(context, expressionSyntax);
                                    continue;
                                }

                                // Look for ".TabIndex = <x>"
                                if (expressionStatementOperation.Operation is IAssignmentOperation assignmentOperation)
                                {
                                    ParseTabIndexAssignments(context, (AssignmentExpressionSyntax)assignmentOperation.Syntax);
                                    continue;
                                }

                            }
                            break;

                        default:
                            break;
                    }
                }

                Diagnostic diagnostic = Diagnostic.Create(InconsistentTabIndexRule, Location.None, operationBlock.ToString());
                context.ReportDiagnostic(diagnostic);
            }
        }

        private void ParseControlAddStatements(OperationBlockAnalysisContext context, InvocationExpressionSyntax expressionSyntax)
        {
            if (!expressionSyntax.Expression.ToString().EndsWith(".Controls.Add"))
            {
                return;
            }

            var syntax = expressionSyntax.Expression;

            // this.Controls.Add(this.button2) --> this.button2
            ArgumentSyntax? argumentSyntax = expressionSyntax.ArgumentList.Arguments.FirstOrDefault();
            if (argumentSyntax is null)
            {
                return;
            }

            // this is something like "this.Controls.Add" or "panel1.Controls.Add", but good enough for our intents and purposes
            string container = syntax.ToString();

            if (!_controlsAddIndex.ContainsKey(container))
            {
                _controlsAddIndex[container] = new List<string>();
            }

            // button2
            string? controlName = GetControlName(argumentSyntax.Expression);
            if (controlName is null)
            {
                return;
            }

            _controlsAddIndex[container].Add(controlName);
        }

        private void ParseTabIndexAssignments(OperationBlockAnalysisContext context, AssignmentExpressionSyntax expressionSyntax)
        {
            var propertyNameExpressionSyntax = (MemberAccessExpressionSyntax)expressionSyntax.Left;
            SimpleNameSyntax propertyNameSyntax = propertyNameExpressionSyntax.Name;

            if (propertyNameSyntax.Identifier.ValueText != "TabIndex")
            {
                return;
            }

            string? controlName = GetControlName(propertyNameExpressionSyntax.Expression);
            if (controlName is null)
            {
                Debug.Fail("How did we get here?");
                return;
            }

            if (expressionSyntax.Right.Kind() != Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression)
            {
                Diagnostic diagnostic1 = Diagnostic.Create(NonNumericTabIndexValueRule,
                    Location.Create(expressionSyntax.Right.SyntaxTree, expressionSyntax.Right.Span),
                    controlName);
                context.ReportDiagnostic(diagnostic1);
                return;
            }

            var propertyValueExpressionSyntax = (LiteralExpressionSyntax)expressionSyntax.Right;
            int tabIndexValue = (int)propertyValueExpressionSyntax.Token.Value;

            // "button3:0"
            _controlsTabIndex[controlName] = tabIndexValue;
        }

        private static string? GetControlName(ExpressionSyntax expressionSyntax)
            => expressionSyntax switch
            {
                // local variable, e.g. "button3.TabIndex = 0" --> "button3";
                IdentifierNameSyntax identifierNameSyntax => identifierNameSyntax.Identifier.ValueText,

                // field, e.g. "this.button1.TabIndex = 1" --> "button1";
                MemberAccessExpressionSyntax controlNameExpressionSyntax => controlNameExpressionSyntax.ToString(),

                _ => null,
            };
    }
}
