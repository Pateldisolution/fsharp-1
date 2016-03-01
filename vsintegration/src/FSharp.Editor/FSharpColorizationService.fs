﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Classification
open Microsoft.CodeAnalysis.Editor
open Microsoft.CodeAnalysis.Editor.Implementation.Classification
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Host.Mef
open Microsoft.CodeAnalysis.Text

open Microsoft.VisualStudio.FSharp.LanguageService
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Tagging

open Microsoft.FSharp.Compiler.SourceCodeServices

// TODO: add types colorization if available from intellisense
// TODO: add defines flags if available from project sites and files

type internal SourceTextColorizationData(classificationData: seq<ClassifiedSpan>) =
    member this.Tokens = classificationData |> Seq.toArray
    member this.GetClassifiedSpan(position: int): Option<ClassifiedSpan> =
        let mutable left = 0
        let mutable right = this.Tokens.Length - 1
        let mutable result = None
        while result.IsNone && right >= left do
            let middle = (left + right) / 2
            let middleToken = this.Tokens.[middle]
            if middleToken.TextSpan.End <= position then
                left <- middle + 1
            else if middleToken.TextSpan.Start > position then
                right <- middle - 1
            else
                result <- Some(middleToken)
        result

[<ExportLanguageService(typeof<IEditorClassificationService>, FSharpCommonConstants.FSharpLanguageName)>]
type internal FSharpColorizationService() =

    static let colorizationDataCache = ConditionalWeakTable<(SourceText * TextSpan * Option<string>), SourceTextColorizationData>()
    
    static let scanSourceText(sourceText: SourceText, textSpan: TextSpan, fileName: Option<string>, defines: string list, cancellationToken: CancellationToken): SourceTextColorizationData =
        let mutable runningLexState = ref(0L)
        let result = new List<ClassifiedSpan>()
        let sourceTokenizer = FSharpSourceTokenizer(defines, fileName)
        
        let compilerTokenToRoslynToken(colorKind: FSharpTokenColorKind) = 
            match colorKind with
            | FSharpTokenColorKind.Comment -> ClassificationTypeNames.Comment
            | FSharpTokenColorKind.Identifier -> ClassificationTypeNames.Identifier
            | FSharpTokenColorKind.Keyword -> ClassificationTypeNames.Keyword
            | FSharpTokenColorKind.String -> ClassificationTypeNames.StringLiteral
            | FSharpTokenColorKind.Text -> ClassificationTypeNames.Text
            | FSharpTokenColorKind.UpperIdentifier -> ClassificationTypeNames.Identifier
            | FSharpTokenColorKind.Number -> ClassificationTypeNames.NumericLiteral
            | FSharpTokenColorKind.InactiveCode -> ClassificationTypeNames.ExcludedCode 
            | FSharpTokenColorKind.PreprocessorKeyword -> ClassificationTypeNames.PreprocessorKeyword 
            | FSharpTokenColorKind.Operator -> ClassificationTypeNames.Operator
            | FSharpTokenColorKind.TypeName  -> ClassificationTypeNames.ClassName
            | FSharpTokenColorKind.Default | _ -> ClassificationTypeNames.Text

        let scanNextToken(lineTokenizer: FSharpLineTokenizer, colorMap: string[], lexState: Ref<int64>) =
            let tokenInfoOption, currentLexState = lineTokenizer.ScanToken(lexState.Value)
            lexState.Value <- currentLexState
            match tokenInfoOption with
            | None -> false
            | Some(tokenInfo) ->
                let classificationType = compilerTokenToRoslynToken(tokenInfo.ColorClass)
                for i = tokenInfo.LeftColumn to tokenInfo.RightColumn do
                    Array.set colorMap i classificationType
                true

        let scanSourceLine(textLine: TextLine, lexState: Ref<int64>) =
            let lineTokenizer = sourceTokenizer.CreateLineTokenizer(textLine.Text.ToString(textLine.Span))
            let colorMap = Array.create textLine.Span.Length ClassificationTypeNames.Text
            while scanNextToken(lineTokenizer, colorMap, lexState) do ()

            let mutable startPosition = 0
            let mutable endPosition = startPosition
            while startPosition < colorMap.Length do
                let classificationType = colorMap.[startPosition]
                endPosition <- startPosition
                while endPosition < colorMap.Length && classificationType = colorMap.[endPosition] do
                    endPosition <- endPosition + 1
                let textSpan = new TextSpan(textLine.Start + startPosition, endPosition - startPosition)
                result.Add(new ClassifiedSpan(classificationType, textSpan))
                startPosition <- endPosition

        let scanStartLine = sourceText.Lines.GetLineFromPosition(textSpan.Start).LineNumber
        let scanEndLine = sourceText.Lines.GetLineFromPosition(textSpan.End).LineNumber

        for i = scanStartLine to scanEndLine do
            cancellationToken.ThrowIfCancellationRequested()
            let currentLine = sourceText.Lines.Item(i)
            scanSourceLine(currentLine, runningLexState)

        SourceTextColorizationData(result)
        
    static let classifySourceTextAsync(sourceText: SourceText, fileName: Option<string>, textSpan: TextSpan, result: List<ClassifiedSpan>, cancellationToken: CancellationToken) =
        Task.Run(fun () ->
            try
                let classificationData = FSharpColorizationService.GetColorizationData(sourceText, textSpan, fileName, [], cancellationToken)
                result.AddRange(classificationData.Tokens |> Seq.filter(fun token -> textSpan.Start <= token.TextSpan.Start && token.TextSpan.End <= textSpan.End))
            with ex -> 
                Assert.Exception(ex)
                reraise()  
        )

    interface IEditorClassificationService with
        
        member this.AddLexicalClassifications(text: SourceText, textSpan: TextSpan, result: List<ClassifiedSpan>, cancellationToken: CancellationToken) =
            classifySourceTextAsync(text, None, textSpan, result, cancellationToken).Wait(cancellationToken)
        
        member this.AddSyntacticClassificationsAsync(document: Document, textSpan: TextSpan, result: List<ClassifiedSpan>, cancellationToken: CancellationToken) =
            let sourceText = document.GetTextAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult()
            classifySourceTextAsync(sourceText, Some(document.Name), textSpan, result, cancellationToken)

        member this.AddSemanticClassificationsAsync(document: Document, textSpan: TextSpan, result: List<ClassifiedSpan>, cancellationToken: CancellationToken) =
            let sourceText = document.GetTextAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult()
            classifySourceTextAsync(sourceText, Some(document.Name), textSpan, result, cancellationToken)

        member this.AdjustStaleClassification(text: SourceText, classifiedSpan: ClassifiedSpan) : ClassifiedSpan =
            let result = new List<ClassifiedSpan>()
            classifySourceTextAsync(text, None, classifiedSpan.TextSpan, result, CancellationToken.None).Wait()
            if result.Any() then
                result.First()
            else
                new ClassifiedSpan(ClassificationTypeNames.WhiteSpace, classifiedSpan.TextSpan)

    // Helper function to proxy Roslyn types to tests
    static member GetColorizationData(sourceText: SourceText, textSpan: TextSpan, fileName: Option<string>, defines: string list, cancellationToken: CancellationToken) : SourceTextColorizationData =
        colorizationDataCache.GetValue((sourceText, textSpan, fileName), fun key -> scanSourceText(sourceText, textSpan, fileName, defines, cancellationToken))
