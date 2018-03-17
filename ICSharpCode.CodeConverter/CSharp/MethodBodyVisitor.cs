﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VBasic = Microsoft.CodeAnalysis.VisualBasic;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.CodeConverter.CSharp
{
    public partial class VisualBasicConverter
    {
        class MethodBodyVisitor : VBasic.VisualBasicSyntaxVisitor<SyntaxList<StatementSyntax>>
        {
            SemanticModel semanticModel;
            readonly VBasic.VisualBasicSyntaxVisitor<CSharpSyntaxNode> nodesVisitor;
            private readonly Stack<string> withBlockTempVariableNames;

            public bool IsIterator { get; set; }
            public VBasic.VisualBasicSyntaxVisitor<SyntaxList<StatementSyntax>> CommentConvertingVisitor { get; }

            public MethodBodyVisitor(SemanticModel semanticModel, VBasic.VisualBasicSyntaxVisitor<CSharpSyntaxNode> nodesVisitor, Stack<string> withBlockTempVariableNames, TriviaConverter triviaConverter)
            {
                this.semanticModel = semanticModel;
                this.nodesVisitor = nodesVisitor;
                this.withBlockTempVariableNames = withBlockTempVariableNames;
                CommentConvertingVisitor = new CommentConvertingMethodBodyVisitor(this, triviaConverter);
            }

            public override SyntaxList<StatementSyntax> DefaultVisit(SyntaxNode node)
            {
                var nodeString = node.ToString();
                if (nodeString.Length > 15) {
                    nodeString = nodeString.Substring(0, 12) + "...";
                }
                
                throw new NotImplementedException(node.GetType() + $" not implemented - cannot convert {nodeString}");
            }

            public override SyntaxList<StatementSyntax> VisitStopOrEndStatement(VBSyntax.StopOrEndStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.ParseStatement(ConvertStopOrEndToCSharpStatementText(node)));
            }

            private static string ConvertStopOrEndToCSharpStatementText(VBSyntax.StopOrEndStatementSyntax node)
            {
                switch (VBasic.VisualBasicExtensions.Kind(node.StopOrEndKeyword)) {
                    case VBasic.SyntaxKind.StopKeyword:
                        return "System.Diagnostics.Debugger.Break();";
                    case VBasic.SyntaxKind.EndKeyword:
                        return "System.Environment.Exit(0);";
                    default:
                        throw new NotImplementedException(node.StopOrEndKeyword.Kind() + " not implemented!");
                }
            }

            public override SyntaxList<StatementSyntax> VisitLocalDeclarationStatement(VBSyntax.LocalDeclarationStatementSyntax node)
            {
                var modifiers = ConvertModifiers(node.Modifiers, TokenContext.Local);

                var declarations = new List<LocalDeclarationStatementSyntax>();

                foreach (var declarator in node.Declarators)
                    foreach (var decl in SplitVariableDeclarations(declarator, nodesVisitor, semanticModel))
                        declarations.Add(SyntaxFactory.LocalDeclarationStatement(modifiers, decl.Value));

                return SyntaxFactory.List<StatementSyntax>(declarations);
            }

            public override SyntaxList<StatementSyntax> VisitAddRemoveHandlerStatement(VBSyntax.AddRemoveHandlerStatementSyntax node)
            {
                var syntaxKind = ConvertAddRemoveHandlerToCSharpSyntaxKind(node);
                return SingleStatement(SyntaxFactory.AssignmentExpression(syntaxKind,
                    (ExpressionSyntax)node.EventExpression.Accept(nodesVisitor),
                    (ExpressionSyntax)node.DelegateExpression.Accept(nodesVisitor)));
            }

            private static SyntaxKind ConvertAddRemoveHandlerToCSharpSyntaxKind(VBSyntax.AddRemoveHandlerStatementSyntax node)
            {
                switch (node.Kind()) {
                    case VBasic.SyntaxKind.AddHandlerStatement:
                        return SyntaxKind.AddAssignmentExpression;
                    case VBasic.SyntaxKind.RemoveHandlerStatement:
                        return SyntaxKind.SubtractAssignmentExpression;
                    default:
                        throw new NotImplementedException(node.Kind() + " not implemented!");
                }
            }

            public override SyntaxList<StatementSyntax> VisitExpressionStatement(VBSyntax.ExpressionStatementSyntax node)
            {
                return SingleStatement((ExpressionSyntax)node.Expression.Accept(nodesVisitor));
            }

            public override SyntaxList<StatementSyntax> VisitAssignmentStatement(VBSyntax.AssignmentStatementSyntax node)
            {
                var kind = ConvertToken(node.Kind(), TokenContext.Local);
                return SingleStatement(SyntaxFactory.AssignmentExpression(kind, (ExpressionSyntax)node.Left.Accept(nodesVisitor), (ExpressionSyntax)node.Right.Accept(nodesVisitor)));
            }

            public override SyntaxList<StatementSyntax> VisitEraseStatement(VBSyntax.EraseStatementSyntax node)
            {
                var eraseStatements = node.Expressions.Select<VBSyntax.ExpressionSyntax, StatementSyntax>(arrayExpression => {
                    var lhs = arrayExpression.Accept(nodesVisitor);
                    var rhs = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                    var assignmentExpressionSyntax =
                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, (ExpressionSyntax)lhs,
                            rhs);
                    return SyntaxFactory.ExpressionStatement(assignmentExpressionSyntax);
                });
                return SyntaxFactory.List(eraseStatements);
            }

            public override SyntaxList<StatementSyntax> VisitReDimStatement(VBSyntax.ReDimStatementSyntax node)
            {
                return SyntaxFactory.List(node.Clauses.SelectMany(arrayExpression => arrayExpression.Accept(CommentConvertingVisitor)));
            }

            public override SyntaxList<StatementSyntax> VisitRedimClause(VBSyntax.RedimClauseSyntax node)
            {
                bool preserve = node.Parent is VBSyntax.ReDimStatementSyntax rdss && rdss.PreserveKeyword.IsKind(VBasic.SyntaxKind.PreserveKeyword);
                
                var csTargetArrayExpression = (ExpressionSyntax) node.Expression.Accept(nodesVisitor);
                var convertedBounds = ConvertArrayBounds(node.ArrayBounds, semanticModel, nodesVisitor).ToList();

                var newArrayAssignment = CreateNewArrayAssignment(node.Expression, csTargetArrayExpression, convertedBounds, node.SpanStart);
                if (!preserve) return SingleStatement(newArrayAssignment);
                
                var oldTargetName = GetUniqueVariableNameInScope(node, "old" + csTargetArrayExpression.ToString().ToPascalCase());
                var oldArrayAssignment = CreateVariableDeclarationAndAssignment(oldTargetName, csTargetArrayExpression);

                var oldTargetExpression = SyntaxFactory.IdentifierName(oldTargetName);
                var arrayCopyIfNotNull = CreateConditionalRankOneArrayCopy(oldTargetExpression, csTargetArrayExpression, convertedBounds);

                return SyntaxFactory.List(new StatementSyntax[] {oldArrayAssignment, newArrayAssignment, arrayCopyIfNotNull});
            }

            /// <summary>
            /// Cut down version of Microsoft.VisualBasic.CompilerServices.Utils.CopyArray
            /// </summary>
            private static IfStatementSyntax CreateConditionalRankOneArrayCopy(IdentifierNameSyntax oldTargetExpression,
                ExpressionSyntax csTargetArrayExpression,
                List<ExpressionSyntax> convertedBounds)
            {
                var expressionSyntax = convertedBounds.Count != 1 ? throw new NotSupportedException("ReDim not supported with Preserve for multi-dimensional arrays")
                    : convertedBounds.Single();
                var oldTargetLength = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    oldTargetExpression, SyntaxFactory.IdentifierName("Length"));
                var minLength = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("Math.Min"),
                    CreateArgList(expressionSyntax, oldTargetLength));
                var oldTargetNotEqualToNull = SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, oldTargetExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                var copyArgList = CreateArgList(oldTargetExpression, csTargetArrayExpression, minLength);
                var arrayCopy = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("Array.Copy"), copyArgList);
                return SyntaxFactory.IfStatement(oldTargetNotEqualToNull, SyntaxFactory.ExpressionStatement(arrayCopy));
            }

            private ExpressionStatementSyntax CreateNewArrayAssignment(VBSyntax.ExpressionSyntax vbArrayExpression,
                ExpressionSyntax csArrayExpression, List<ExpressionSyntax> convertedBounds,
                int nodeSpanStart)
            {
                var arrayRankSpecifierSyntax = SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SeparatedList(convertedBounds));
                var convertedType = (IArrayTypeSymbol) semanticModel.GetTypeInfo(vbArrayExpression).ConvertedType;
                var typeSyntax = GetTypeSyntaxFromTypeSymbol(convertedType.ElementType, nodeSpanStart);
                var arrayCreation =
                    SyntaxFactory.ArrayCreationExpression(SyntaxFactory.ArrayType(typeSyntax,
                        SyntaxFactory.SingletonList(arrayRankSpecifierSyntax)));
                var assignmentExpressionSyntax =
                    SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, csArrayExpression, arrayCreation);
                var newArrayAssignment = SyntaxFactory.ExpressionStatement(assignmentExpressionSyntax);
                return newArrayAssignment;
            }

            private static ArgumentListSyntax CreateArgList(params ExpressionSyntax[] copyArgs)
            {
                return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(copyArgs.Select(SyntaxFactory.Argument)));
            }

            private TypeSyntax GetTypeSyntaxFromTypeSymbol(ITypeSymbol convertedType, int nodeSpanStart)
            {
                var predefinedKeywordKind = convertedType.SpecialType.GetPredefinedKeywordKind();
                if (predefinedKeywordKind != SyntaxKind.None) return SyntaxFactory.PredefinedType(SyntaxFactory.Token(predefinedKeywordKind));
                return SyntaxFactory.ParseTypeName(convertedType.ToMinimalDisplayString(semanticModel, nodeSpanStart));
            }

            public override SyntaxList<StatementSyntax> VisitThrowStatement(VBSyntax.ThrowStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.ThrowStatement((ExpressionSyntax)node.Expression?.Accept(nodesVisitor)));
            }

            public override SyntaxList<StatementSyntax> VisitReturnStatement(VBSyntax.ReturnStatementSyntax node)
            {
                if (IsIterator)
                    return SingleStatement(SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement));
                return SingleStatement(SyntaxFactory.ReturnStatement((ExpressionSyntax)node.Expression?.Accept(nodesVisitor)));
            }

            public override SyntaxList<StatementSyntax> VisitContinueStatement(VBSyntax.ContinueStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.ContinueStatement());
            }

            public override SyntaxList<StatementSyntax> VisitYieldStatement(VBSyntax.YieldStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.YieldStatement(SyntaxKind.YieldReturnStatement, (ExpressionSyntax)node.Expression?.Accept(nodesVisitor)));
            }

            public override SyntaxList<StatementSyntax> VisitExitStatement(VBSyntax.ExitStatementSyntax node)
            {
                switch (VBasic.VisualBasicExtensions.Kind(node.BlockKeyword)) {
                    case VBasic.SyntaxKind.SubKeyword:
                        return SingleStatement(SyntaxFactory.ReturnStatement());
                    case VBasic.SyntaxKind.FunctionKeyword:
                        VBasic.VisualBasicSyntaxNode typeContainer = (VBasic.VisualBasicSyntaxNode)node.Ancestors().OfType<VBSyntax.LambdaExpressionSyntax>().FirstOrDefault()
                            ?? node.Ancestors().OfType<VBSyntax.MethodBlockSyntax>().FirstOrDefault();
                        var info = typeContainer.TypeSwitch(
                            (VBSyntax.LambdaExpressionSyntax e) => semanticModel.GetTypeInfo(e).Type.GetReturnType(),
                            (VBSyntax.MethodBlockSyntax e) => {
                                var type = (TypeSyntax)e.SubOrFunctionStatement.AsClause?.Type.Accept(nodesVisitor) ?? SyntaxFactory.ParseTypeName("object");
                                return semanticModel.GetSymbolInfo(type).Symbol?.GetReturnType();
                            }
                        );
                        ExpressionSyntax expr;
                        if (info == null)
                            expr = null;
                        else if (info.IsReferenceType)
                            expr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
                        else if (info.CanBeReferencedByName)
                            expr = SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(info.ToMinimalDisplayString(semanticModel, node.SpanStart)));
                        else
                            throw new NotSupportedException();
                        return SingleStatement(SyntaxFactory.ReturnStatement(expr));
                    default:
                        return SingleStatement(SyntaxFactory.BreakStatement());
                }
            }

            public override SyntaxList<StatementSyntax> VisitRaiseEventStatement(VBSyntax.RaiseEventStatementSyntax node)
            {
                return SingleStatement(
                    SyntaxFactory.ConditionalAccessExpression(
                        (ExpressionSyntax)node.Name.Accept(nodesVisitor),
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("Invoke")),
                            (ArgumentListSyntax)node.ArgumentList.Accept(nodesVisitor)
                        )
                    )
                );
            }

            public override SyntaxList<StatementSyntax> VisitSingleLineIfStatement(VBSyntax.SingleLineIfStatementSyntax node)
            {
                var condition = (ExpressionSyntax)node.Condition.Accept(nodesVisitor);
                var block = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                ElseClauseSyntax elseClause = null;

                if (node.ElseClause != null) {
                    var elseBlock = SyntaxFactory.Block(node.ElseClause.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                    elseClause = SyntaxFactory.ElseClause(elseBlock.UnpackNonNestedBlock());
                }
                return SingleStatement(SyntaxFactory.IfStatement(condition, block.UnpackNonNestedBlock(), elseClause));
            }

            public override SyntaxList<StatementSyntax> VisitMultiLineIfBlock(VBSyntax.MultiLineIfBlockSyntax node)
            {
                var condition = (ExpressionSyntax)node.IfStatement.Condition.Accept(nodesVisitor);
                var block = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                ElseClauseSyntax elseClause = null;

                if (node.ElseBlock != null) {
                    var elseBlock = SyntaxFactory.Block(node.ElseBlock.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                    elseClause = SyntaxFactory.ElseClause(elseBlock.UnpackPossiblyNestedBlock());// so that you get a neat "else if" at the end
                }

                foreach (var elseIf in node.ElseIfBlocks.Reverse()) {
                    var elseBlock = SyntaxFactory.Block(elseIf.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                    var ifStmt = SyntaxFactory.IfStatement((ExpressionSyntax)elseIf.ElseIfStatement.Condition.Accept(nodesVisitor), elseBlock.UnpackNonNestedBlock(), elseClause);
                    elseClause = SyntaxFactory.ElseClause(ifStmt);
                }

                return SingleStatement(SyntaxFactory.IfStatement(condition, block.UnpackNonNestedBlock(), elseClause));
            }

            public override SyntaxList<StatementSyntax> VisitForBlock(VBSyntax.ForBlockSyntax node)
            {
                var stmt = node.ForStatement;
                ExpressionSyntax startValue = (ExpressionSyntax)stmt.FromValue.Accept(nodesVisitor);
                VariableDeclarationSyntax declaration = null;
                ExpressionSyntax id;
                if (stmt.ControlVariable is VBSyntax.VariableDeclaratorSyntax) {
                    var v = (VBSyntax.VariableDeclaratorSyntax)stmt.ControlVariable;
                    declaration = SplitVariableDeclarations(v, nodesVisitor, semanticModel).Values.Single();
                    declaration = declaration.WithVariables(SyntaxFactory.SingletonSeparatedList(declaration.Variables[0].WithInitializer(SyntaxFactory.EqualsValueClause(startValue))));
                    id = SyntaxFactory.IdentifierName(declaration.Variables[0].Identifier);
                } else {
                    id = (ExpressionSyntax)stmt.ControlVariable.Accept(nodesVisitor);
                    var symbol = semanticModel.GetSymbolInfo(stmt.ControlVariable).Symbol;
                    if (!semanticModel.LookupSymbols(node.FullSpan.Start, name: symbol.Name).Any()) {
                        var variableDeclaratorSyntax = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(symbol.Name), null,
                            SyntaxFactory.EqualsValueClause(startValue));
                        declaration = SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(variableDeclaratorSyntax));
                    } else {
                        startValue = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, id, startValue);
                    }
                }

                var step = (ExpressionSyntax)stmt.StepClause?.StepValue.Accept(nodesVisitor);
                PrefixUnaryExpressionSyntax value = step.SkipParens() as PrefixUnaryExpressionSyntax;
                ExpressionSyntax condition;
                if (value == null) {
                    condition = SyntaxFactory.BinaryExpression(SyntaxKind.LessThanOrEqualExpression, id, (ExpressionSyntax)stmt.ToValue.Accept(nodesVisitor));
                } else {
                    condition = SyntaxFactory.BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression, id, (ExpressionSyntax)stmt.ToValue.Accept(nodesVisitor));
                }
                if (step == null)
                    step = SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, id);
                else
                    step = SyntaxFactory.AssignmentExpression(SyntaxKind.AddAssignmentExpression, id, step);
                var block = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                return SingleStatement(SyntaxFactory.ForStatement(
                    declaration,
                    declaration != null ? SyntaxFactory.SeparatedList<ExpressionSyntax>() : SyntaxFactory.SingletonSeparatedList(startValue),
                    condition,
                    SyntaxFactory.SingletonSeparatedList(step),
                    block.UnpackNonNestedBlock()));
            }

            public override SyntaxList<StatementSyntax> VisitForEachBlock(VBSyntax.ForEachBlockSyntax node)
            {
                var stmt = node.ForEachStatement;

                TypeSyntax type = null;
                SyntaxToken id;
                if (stmt.ControlVariable is VBSyntax.VariableDeclaratorSyntax) {
                    var v = (VBSyntax.VariableDeclaratorSyntax)stmt.ControlVariable;
                    var declaration = SplitVariableDeclarations(v, nodesVisitor, semanticModel).Values.Single();
                    type = declaration.Type;
                    id = declaration.Variables[0].Identifier;
                } else {
                    var v = (IdentifierNameSyntax)stmt.ControlVariable.Accept(nodesVisitor);
                    id = v.Identifier;
                    type = SyntaxFactory.ParseTypeName("var");
                }

                var block = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                return SingleStatement(SyntaxFactory.ForEachStatement(
                        type,
                        id,
                        (ExpressionSyntax)stmt.Expression.Accept(nodesVisitor),
                        block.UnpackNonNestedBlock()
                    ));
            }


            public override SyntaxList<StatementSyntax> VisitLabelStatement(VBSyntax.LabelStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.LabeledStatement(node.LabelToken.Text, SyntaxFactory.EmptyStatement()));
            }

            public override SyntaxList<StatementSyntax> VisitGoToStatement(VBSyntax.GoToStatementSyntax node)
            {
                return SingleStatement(SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement,
                    SyntaxFactory.IdentifierName(node.Label.LabelToken.Text)));
            }

            public override SyntaxList<StatementSyntax> VisitSelectBlock(VBSyntax.SelectBlockSyntax node)
            {
                var expr = (ExpressionSyntax)node.SelectStatement.Expression.Accept(nodesVisitor);
                var exprWithoutTrivia = expr.WithoutTrivia().WithoutAnnotations();
                var sections = new List<SwitchSectionSyntax>();
                foreach (var block in node.CaseBlocks) {
                    var labels = new List<SwitchLabelSyntax>();
                    foreach (var c in block.CaseStatement.Cases) {
                        if (c is VBSyntax.SimpleCaseClauseSyntax s) {
                            labels.Add(SyntaxFactory.CaseSwitchLabel((ExpressionSyntax)s.Value.Accept(nodesVisitor)));
                        } else if (c is VBSyntax.ElseCaseClauseSyntax) {
                            labels.Add(SyntaxFactory.DefaultSwitchLabel());
                        } else if (c is VBSyntax.RelationalCaseClauseSyntax relational) {
                            var discardPatternMatch = SyntaxFactory.DeclarationPattern(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)), SyntaxFactory.DiscardDesignation());
                            var operatorKind = VBasic.VisualBasicExtensions.Kind(relational);
                            var cSharpSyntaxNode = SyntaxFactory.BinaryExpression(ConvertToken(operatorKind, TokenContext.Local), exprWithoutTrivia, (ExpressionSyntax) relational.Value.Accept(nodesVisitor));
                            labels.Add(SyntaxFactory.CasePatternSwitchLabel(discardPatternMatch, SyntaxFactory.WhenClause(cSharpSyntaxNode), SyntaxFactory.Token(SyntaxKind.ColonToken)));
                        } else if (c is VBSyntax.RangeCaseClauseSyntax range) {
                            var discardPatternMatch = SyntaxFactory.DeclarationPattern(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)), SyntaxFactory.DiscardDesignation());
                            var lowerBoundCheck = SyntaxFactory.BinaryExpression(SyntaxKind.LessThanOrEqualExpression, (ExpressionSyntax) range.LowerBound.Accept(nodesVisitor), exprWithoutTrivia);
                            var upperBoundCheck = SyntaxFactory.BinaryExpression(SyntaxKind.LessThanOrEqualExpression, exprWithoutTrivia, (ExpressionSyntax) range.UpperBound.Accept(nodesVisitor));
                            var withinBounds = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, lowerBoundCheck, upperBoundCheck);
                            labels.Add(SyntaxFactory.CasePatternSwitchLabel(discardPatternMatch, SyntaxFactory.WhenClause(withinBounds), SyntaxFactory.Token(SyntaxKind.ColonToken)));
                        } else throw new NotSupportedException(c.Kind().ToString());
                    }

                    var csBlockStatements = block.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)).ToList();
                    if (csBlockStatements.LastOrDefault()
                            ?.IsKind(SyntaxKind.ReturnStatement) != true) {
                        csBlockStatements.Add(SyntaxFactory.BreakStatement());
                    }
                    var list = SingleStatement(SyntaxFactory.Block(csBlockStatements));
                    sections.Add(SyntaxFactory.SwitchSection(SyntaxFactory.List(labels), list));
                }

                var switchStatementSyntax = SyntaxFactory.SwitchStatement(expr, SyntaxFactory.List(sections));
                return SingleStatement(switchStatementSyntax);
            }

            public override SyntaxList<StatementSyntax> VisitWithBlock(VBSyntax.WithBlockSyntax node)
            {
                var withExpression = (ExpressionSyntax)node.WithStatement.Expression.Accept(nodesVisitor);
                withBlockTempVariableNames.Push(GetUniqueVariableNameInScope(node, "withBlock"));
                try {
                    var declaration = CreateVariableDeclarationAndAssignment(withBlockTempVariableNames.Peek(), withExpression);
                    var statements = node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor));

                    return SingleStatement(SyntaxFactory.Block(new[] { declaration }.Concat(statements).ToArray()));
                } finally {
                    withBlockTempVariableNames.Pop();
                }
            }

            private LocalDeclarationStatementSyntax CreateVariableDeclarationAndAssignment(string variableName, ExpressionSyntax initValue)
            {
                var variableDeclaratorSyntax = SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(variableName), null,
                    SyntaxFactory.EqualsValueClause(initValue));
                return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(variableDeclaratorSyntax)));
            }

            private string GetUniqueVariableNameInScope(SyntaxNode node, string variableNameBase)
            {
                var reservedNames = withBlockTempVariableNames.Concat(node.DescendantNodesAndSelf()
                    .SelectMany(syntaxNode => semanticModel.LookupSymbols(syntaxNode.SpanStart).Select(s => s.Name)));
                return NameGenerator.EnsureUniqueness(variableNameBase, reservedNames, true);
            }

            public override SyntaxList<StatementSyntax> VisitTryBlock(VBSyntax.TryBlockSyntax node)
            {
                var block = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                return SingleStatement(
                    SyntaxFactory.TryStatement(
                        block,
                        SyntaxFactory.List(node.CatchBlocks.Select(c => (CatchClauseSyntax)c.Accept(nodesVisitor))),
                        (FinallyClauseSyntax)node.FinallyBlock?.Accept(nodesVisitor)
                    )
                );
            }

            public override SyntaxList<StatementSyntax> VisitSyncLockBlock(VBSyntax.SyncLockBlockSyntax node)
            {
                return SingleStatement(SyntaxFactory.LockStatement(
                    (ExpressionSyntax)node.SyncLockStatement.Expression.Accept(nodesVisitor),
                    SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock()
                ));
            }

            public override SyntaxList<StatementSyntax> VisitUsingBlock(VBSyntax.UsingBlockSyntax node)
            {
                var statementSyntax = SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor)));
                if (node.UsingStatement.Expression == null) {
                    StatementSyntax stmt = statementSyntax;
                    foreach (var v in node.UsingStatement.Variables.Reverse())
                        foreach (var declaration in SplitVariableDeclarations(v, nodesVisitor, semanticModel).Values.Reverse())
                            stmt = SyntaxFactory.UsingStatement(declaration, null, stmt);
                    return SingleStatement(stmt);
                }

                var expr = (ExpressionSyntax)node.UsingStatement.Expression.Accept(nodesVisitor);
                var unpackPossiblyNestedBlock = statementSyntax.UnpackPossiblyNestedBlock(); // Allow reduced indentation for multiple usings in a row
                return SingleStatement(SyntaxFactory.UsingStatement(null, expr, unpackPossiblyNestedBlock));
            }

            public override SyntaxList<StatementSyntax> VisitWhileBlock(VBSyntax.WhileBlockSyntax node)
            {
                return SingleStatement(SyntaxFactory.WhileStatement(
                    (ExpressionSyntax)node.WhileStatement.Condition.Accept(nodesVisitor),
                    SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock()
                ));
            }

            public override SyntaxList<StatementSyntax> VisitDoLoopBlock(VBSyntax.DoLoopBlockSyntax node)
            {
                if (node.DoStatement.WhileOrUntilClause != null) {
                    var stmt = node.DoStatement.WhileOrUntilClause;
                    if (SyntaxTokenExtensions.IsKind(stmt.WhileOrUntilKeyword, VBasic.SyntaxKind.WhileKeyword))
                        return SingleStatement(SyntaxFactory.WhileStatement(
                            (ExpressionSyntax)stmt.Condition.Accept(nodesVisitor),
                            SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock()
                        ));
                    return SingleStatement(SyntaxFactory.WhileStatement(
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, (ExpressionSyntax)stmt.Condition.Accept(nodesVisitor)),
                        SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock()
                    ));
                }
                if (node.LoopStatement.WhileOrUntilClause != null) {
                    var stmt = node.LoopStatement.WhileOrUntilClause;
                    if (SyntaxTokenExtensions.IsKind(stmt.WhileOrUntilKeyword, VBasic.SyntaxKind.WhileKeyword))
                        return SingleStatement(SyntaxFactory.DoStatement(
                            SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock(),
                            (ExpressionSyntax)stmt.Condition.Accept(nodesVisitor)
                        ));
                    return SingleStatement(SyntaxFactory.DoStatement(
                        SyntaxFactory.Block(node.Statements.SelectMany(s => s.Accept(CommentConvertingVisitor))).UnpackNonNestedBlock(),
                        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, (ExpressionSyntax)stmt.Condition.Accept(nodesVisitor))
                    ));
                }
                throw new NotSupportedException();
            }

            public override SyntaxList<StatementSyntax> VisitCallStatement(VBSyntax.CallStatementSyntax node)
            {
                return SingleStatement((ExpressionSyntax) node.Invocation.Accept(nodesVisitor));
            }

            SyntaxList<StatementSyntax> SingleStatement(StatementSyntax statement)
            {
                return SyntaxFactory.SingletonList(statement);
            }

            SyntaxList<StatementSyntax> SingleStatement(ExpressionSyntax expression)
            {
                return SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ExpressionStatement(expression));
            }

            public static IEnumerable<ExpressionSyntax> ConvertArrayBounds(VBSyntax.ArgumentListSyntax argumentListSyntax, SemanticModel model, VBasic.VisualBasicSyntaxVisitor<CSharpSyntaxNode> commentConvertingNodesVisitor)
            {
                return argumentListSyntax.Arguments.Select(a => IncreaseArrayUpperBoundExpression(((VBSyntax.SimpleArgumentSyntax)a).Expression, model, commentConvertingNodesVisitor));
            }

            private static ExpressionSyntax IncreaseArrayUpperBoundExpression(VBSyntax.ExpressionSyntax expr, SemanticModel model, VBasic.VisualBasicSyntaxVisitor<CSharpSyntaxNode> commentConvertingNodesVisitor)
            {
                var constant = model.GetConstantValue(expr);
                if (constant.HasValue && constant.Value is int)
                    return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal((int)constant.Value + 1));

                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.SubtractExpression,
                    (ExpressionSyntax)expr.Accept(commentConvertingNodesVisitor), SyntaxFactory.Token(SyntaxKind.PlusToken), SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
            }
        }
    }

    static class Extensions
    {
        /// <summary>
        /// Returns the single statement in a block if it has no nested statements.
        /// If it has nested statements, and the surrounding block was removed, it could be ambiguous, 
        /// e.g. if (...) { if (...) return null; } else return "";
        /// Unbundling the middle if statement would bind the else to it, rather than the outer if statement
        /// </summary>
        public static StatementSyntax UnpackNonNestedBlock(this BlockSyntax block)
        {
            return block.Statements.Count == 1 && !block.ContainsNestedStatements() ? block.Statements[0] : block;
        }

        /// <summary>
        /// Only use this over <see cref="UnpackNonNestedBlock"/> in special cases where it will display more neatly and where you're sure nested statements don't introduce ambiguity
        /// </summary>
        public static StatementSyntax UnpackPossiblyNestedBlock(this BlockSyntax block)
        {
            return block.Statements.Count == 1 ? block.Statements[0] : block;
        }

        private static bool ContainsNestedStatements(this BlockSyntax block)
        {
            return block.Statements.Any(HasDescendantCSharpStatement);
        }

        private static bool HasDescendantCSharpStatement(this StatementSyntax c)
        {
            return c.DescendantNodes().OfType<StatementSyntax>().Any();
        }
    }
}
