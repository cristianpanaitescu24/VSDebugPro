﻿using Microsoft.VisualStudio.Text;  //text
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor; //Text
using Microsoft.VisualStudio.Text.Operations;       //EditorOperations
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using VSDebugCoreLib.Commands;
using VSDebugPro;

namespace VSDebugPro.VSDebugScript
{
    [Export(typeof(EditorFormatDefinition))]
    [Name("MarkerFormatDefinition/VSDHighlightWordFormatDefinition")]
    [UserVisible(true)]
    internal class VSDHighlightWordFormatDefinition : MarkerFormatDefinition
    {
        public VSDHighlightWordFormatDefinition()
        {
            BackgroundColor = Colors.LightBlue;
            ForegroundColor = Colors.DarkBlue;
            DisplayName = "VSD Highlight Word";
            ZOrder = 5;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [Name("MarkerFormatDefinition/VSDHighlightActionDefinition")]
    [UserVisible(true)]
    internal class VSDHighlightActionDefinition : MarkerFormatDefinition
    {
        public VSDHighlightActionDefinition()
        {
            BackgroundColor = Colors.LightSteelBlue;
            ForegroundColor = Colors.DarkBlue;
            DisplayName = "VSD Highlight Action";
            ZOrder = 5;
        }
    }

    /// <summary>
    /// This is the class that implements the content exposed by this assembly.
    /// </summary>
    internal static class VSDContentTypeDefinition
    {
        public const string ContentType = "vsdscript";

        /// <summary>
        /// Exports the vsd content type
        /// </summary>
        [Export]
        [Name(VSDContentTypeDefinition.ContentType)]
        [BaseDefinition("code")]
        internal static ContentTypeDefinition VSDContentType { get; set; }
    }

    /// <summary>
    /// Derive from TextMarkerTag, in case anyone wants to consume
    /// just the HighlightWordTags by themselves.
    /// </summary>
    public class HighlightWordTag : TextMarkerTag
    {
        public HighlightWordTag() : base("MarkerFormatDefinition/VSDHighlightWordFormatDefinition")
        {
        }
    }

    /// <summary>
    /// Derive from TextMarkerTag, in case anyone wants to consume
    /// just the HighlightActionTags by themselves.
    /// </summary>
    public class HighlightActionTag : TextMarkerTag
    {
        public HighlightActionTag() : base("MarkerFormatDefinition/VSDHighlightActionDefinition")
        {
        }
    }

    /// <summary>
    /// This tagger will provide tags for every word in the buffer that
    /// matches the word currently under the cursor.
    /// </summary>
    public class HighlightWordTagger : ITagger<HighlightWordTag>
    {
        private ITextView View { get; set; }
        private ITextBuffer SourceBuffer { get; set; }
        private ITextSearchService TextSearchService { get; set; }
        private ITextStructureNavigator TextStructureNavigator { get; set; }
        private object updateLock = new object();

        // The current set of words to highlight
        private NormalizedSnapshotSpanCollection WordSpans { get; set; }

        private SnapshotSpan? CurrentWord { get; set; }

        // The current request, from the last cursor movement or view render
        private SnapshotPoint RequestedPoint { get; set; }

        public HighlightWordTagger(ITextView view, ITextBuffer sourceBuffer, ITextSearchService textSearchService,
                                   ITextStructureNavigator textStructureNavigator)
        {
            View = view;

            SourceBuffer = sourceBuffer;
            TextSearchService = textSearchService;
            TextStructureNavigator = textStructureNavigator;

            WordSpans = new NormalizedSnapshotSpanCollection();
            CurrentWord = null;

            // Subscribe to both change events in the view - any time the view is updated
            // or the caret is moved, we refresh our list of highlighted words.
            this.View.Caret.PositionChanged += CaretPositionChanged;
            this.View.LayoutChanged += ViewLayoutChanged;
        }

        #region Event Handlers

        private void ViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // If a new snapshot wasn't generated, then skip this layout
            if (e.NewViewState.EditSnapshot != e.OldViewState.EditSnapshot)
            {
                UpdateAtCaretPosition(View.Caret.Position);
            }
        }

        private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            UpdateAtCaretPosition(e.NewPosition);
        }

        private void UpdateAtCaretPosition(CaretPosition caretPoisition)
        {
            SnapshotPoint? point = caretPoisition.Point.GetPoint(SourceBuffer, caretPoisition.Affinity);

            if (!point.HasValue)
                return;

            // If the new cursor position is still within the current word (and on the same snapshot),
            // we don't need to check it.
            if (CurrentWord.HasValue &&
                CurrentWord.Value.Snapshot == View.TextSnapshot &&
                point.Value >= CurrentWord.Value.Start &&
                point.Value <= CurrentWord.Value.End)
            {
                return;
            }

            RequestedPoint = point.Value;

            ThreadPool.QueueUserWorkItem(UpdateWordAdornments);
        }

        private void UpdateWordAdornments(object threadContext)
        {
            SnapshotPoint currentRequest = RequestedPoint;

            List<SnapshotSpan> wordSpans = new List<SnapshotSpan>();

            // Find all words in the buffer like the one the caret is on
            TextExtent word = TextStructureNavigator.GetExtentOfWord(currentRequest);

            bool foundWord = true;

            // If we've selected something not worth highlighting, we might have
            // missed a "word" by a little bit
            if (!WordExtentIsValid(currentRequest, word))
            {
                // Before we retry, make sure it is worthwhile
                if (word.Span.Start != currentRequest ||
                    currentRequest == currentRequest.GetContainingLine().Start ||
                    char.IsWhiteSpace((currentRequest - 1).GetChar()))
                {
                    foundWord = false;
                }
                else
                {
                    // Try again, one character previous.  If the caret is at the end of a word, then
                    // this will pick up the word we are at the end of.
                    word = TextStructureNavigator.GetExtentOfWord(currentRequest - 1);

                    // If we still aren't valid the second time around, we're done
                    if (!WordExtentIsValid(currentRequest, word))
                        foundWord = false;
                }
            }

            if (!foundWord)
            {
                // If we couldn't find a word, just clear out the existing markers
                SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(), null);
                return;
            }

            SnapshotSpan currentWord = word.Span;

            // If this is the same word we currently have, we're done (e.g. caret moved within a word).
            if (CurrentWord.HasValue && currentWord == CurrentWord)
                return;

            // Find the new spans
            FindData findData = new FindData(currentWord.GetText(), currentWord.Snapshot);
            findData.FindOptions = FindOptions.WholeWord | FindOptions.MatchCase;

            wordSpans.AddRange(TextSearchService.FindAll(findData));

            // If we are still up-to-date (another change hasn't happened yet), do a real update
            if (currentRequest == RequestedPoint)
                SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(wordSpans), currentWord);
        }

        /// <summary>
        /// Determine if a given "word" should be highlighted
        /// </summary>
        private static bool WordExtentIsValid(SnapshotPoint currentRequest, TextExtent word)
        {
            string currentWord = currentRequest.Snapshot.GetText(word.Span);

            foreach (IConsoleCommand command in VSDebugProPackage.Context.Commands)
            {
                if (command.CommandString != string.Empty && command.CommandString == currentWord)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Perform a synchronous update, in case multiple background threads are running
        /// </summary>
        private void SynchronousUpdate(SnapshotPoint currentRequest, NormalizedSnapshotSpanCollection newSpans, SnapshotSpan? newCurrentWord)
        {
            lock (updateLock)
            {
                if (currentRequest != RequestedPoint)
                    return;

                WordSpans = newSpans;
                CurrentWord = newCurrentWord;

                var tempEvent = TagsChanged;
                if (tempEvent != null)
                    tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }
        }

        #endregion Event Handlers

        #region ITagger<HighlightWordTag> Members

        public IEnumerable<ITagSpan<HighlightWordTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (CurrentWord == null)
                yield break;

            // Hold on to a "snapshot" of the word spans and current word, so that we maintain the same
            // collection throughout
            SnapshotSpan currentWord = CurrentWord.Value;
            NormalizedSnapshotSpanCollection wordSpans = WordSpans;

            if (spans.Count == 0 || WordSpans.Count == 0)
                yield break;

            // If the requested snapshot isn't the same as the one our words are on, translate our spans
            // to the expected snapshot
            if (spans[0].Snapshot != wordSpans[0].Snapshot)
            {
                wordSpans = new NormalizedSnapshotSpanCollection(
                    wordSpans.Select(span => span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive)));

                currentWord = currentWord.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
            }

            // First, yield back the word the cursor is under (if it overlaps)
            // Note that we'll yield back the same word again in the wordspans collection;
            // the duplication here is expected.
            if (spans.OverlapsWith(new NormalizedSnapshotSpanCollection(currentWord)))
                yield return new TagSpan<HighlightWordTag>(currentWord, new HighlightWordTag());

            // Second, yield all the other words in the file
            foreach (SnapshotSpan span in NormalizedSnapshotSpanCollection.Overlap(spans, wordSpans))
            {
                yield return new TagSpan<HighlightWordTag>(span, new HighlightWordTag());
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #endregion ITagger<HighlightWordTag> Members
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("vsdscript")]
    [TagType(typeof(HighlightWordTag))]
    public class HighlightWordTaggerProvider : IViewTaggerProvider
    {
        #region ITaggerProvider Members

        [Import]
        internal ITextSearchService TextSearchService { get; set; }

        [Import]
        internal ITextStructureNavigatorSelectorService TextStructureNavigatorSelector { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            // Only provide highlighting on the top-level buffer
            if (!buffer.ContentType.IsOfType("vsdscript"))
                return null;

            ITextStructureNavigator textStructureNavigator =
                TextStructureNavigatorSelector.GetTextStructureNavigator(buffer);

            return new HighlightWordTagger(textView, buffer, TextSearchService, textStructureNavigator) as ITagger<T>;
        }

        #endregion ITaggerProvider Members
    }

    /// <summary>
    /// This tagger will provide tags for every word in the buffer that
    /// matches the word currently under the cursor.
    /// </summary>
    public class HighlightActionTagger : ITagger<HighlightActionTag>
    {
        private ITextView View { get; set; }
        private ITextBuffer SourceBuffer { get; set; }
        private ITextSearchService TextSearchService { get; set; }
        private ITextStructureNavigator TextStructureNavigator { get; set; }
        private object updateLock = new object();

        // The current set of words to highlight
        private NormalizedSnapshotSpanCollection WordSpans { get; set; }

        private SnapshotSpan? CurrentWord { get; set; }

        // The current request, from the last cursor movement or view render
        private SnapshotPoint RequestedPoint { get; set; }

        public HighlightActionTagger(ITextView view, ITextBuffer sourceBuffer, ITextSearchService textSearchService,
                                   ITextStructureNavigator textStructureNavigator)
        {
            this.View = view;

            this.SourceBuffer = sourceBuffer;
            this.TextSearchService = textSearchService;
            this.TextStructureNavigator = textStructureNavigator;

            this.WordSpans = new NormalizedSnapshotSpanCollection();
            this.CurrentWord = null;

            // Subscribe to both change events in the view - any time the view is updated
            // or the caret is moved, we refresh our list of highlighted words.
            this.View.Caret.PositionChanged += CaretPositionChanged;
            this.View.LayoutChanged += ViewLayoutChanged;
        }

        #region Event Handlers

        private void ViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            // If a new snapshot wasn't generated, then skip this layout
            if (e.NewViewState.EditSnapshot != e.OldViewState.EditSnapshot)
            {
                UpdateAtCaretPosition(View.Caret.Position);
            }
        }

        private void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            UpdateAtCaretPosition(e.NewPosition);
        }

        private void UpdateAtCaretPosition(CaretPosition caretPoisition)
        {
            SnapshotPoint? point = caretPoisition.Point.GetPoint(SourceBuffer, caretPoisition.Affinity);

            if (!point.HasValue)
                return;

            // If the new cursor position is still within the current word (and on the same snapshot),
            // we don't need to check it.
            if (CurrentWord.HasValue &&
                CurrentWord.Value.Snapshot == View.TextSnapshot &&
                point.Value >= CurrentWord.Value.Start &&
                point.Value <= CurrentWord.Value.End)
            {
                return;
            }

            RequestedPoint = point.Value;

            ThreadPool.QueueUserWorkItem(UpdateWordAdornments);
        }

        private void UpdateWordAdornments(object threadContext)
        {
            try
            {
                SnapshotPoint currentRequest = RequestedPoint;

                List<SnapshotSpan> wordSpans = new List<SnapshotSpan>();

                string clickLineSelection = SourceBuffer.CurrentSnapshot.GetLineFromPosition(RequestedPoint).GetText();
                int lineStartPos = SourceBuffer.CurrentSnapshot.GetLineFromPosition(RequestedPoint).Start.Position;
                int lineCaretPos = RequestedPoint.Position - lineStartPos;

                int fileStartPos = -1;
                int fileEndPos = -1;
                bool foundWord = false;

                if ((fileStartPos = clickLineSelection.IndexOf("file://")) > 0 &&
                    (fileEndPos = clickLineSelection.IndexOf(">")) > 0 &&
                    (fileEndPos > fileStartPos) &&
                    (lineCaretPos > fileStartPos && lineCaretPos < fileEndPos) &&
                    fileStartPos >= 0 &&
                    fileEndPos >= 0)
                {
                    fileStartPos += "file://".Length;

                    string strFilePath = clickLineSelection.Substring(fileStartPos, fileEndPos - fileStartPos);

                    if (File.Exists(strFilePath))
                    {
                        string strExt = Path.GetExtension(strFilePath);

                        foundWord = true;

                        string editorTool = VSDebugProPackage.Context.Settings.GetAssignedTool(strExt);

                        if (editorTool != string.Empty)
                        {
                            Process.Start(editorTool, strFilePath);
                        }
                    }
                }

                if (!foundWord)
                {
                    // If we couldn't find a word, just clear out the existing markers
                    SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(), null);
                    return;
                }

                SnapshotSpan currentWord = new SnapshotSpan(new SnapshotPoint(SourceBuffer.CurrentSnapshot, lineStartPos + fileStartPos),
                                                             new SnapshotPoint(SourceBuffer.CurrentSnapshot, lineStartPos + fileEndPos));

                // If this is the same word we currently have, we're done (e.g. caret moved within a word).
                if (CurrentWord.HasValue && currentWord == CurrentWord)
                    return;

                // Find the new spans
                FindData findData = new FindData(currentWord.GetText(), currentWord.Snapshot);
                findData.FindOptions = FindOptions.WholeWord | FindOptions.MatchCase;

                wordSpans.AddRange(TextSearchService.FindAll(findData));
                // If we are still up-to-date (another change hasn't happened yet), do a real update
                if (currentRequest == RequestedPoint)
                    SynchronousUpdate(currentRequest, new NormalizedSnapshotSpanCollection(wordSpans), currentWord);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Determine if a given "word" should be highlighted
        /// </summary>
        private static bool WordExtentIsValid(SnapshotPoint currentRequest, TextExtent word)
        {
            return false;
        }

        /// <summary>
        /// Perform a synchronous update, in case multiple background threads are running
        /// </summary>
        private void SynchronousUpdate(SnapshotPoint currentRequest, NormalizedSnapshotSpanCollection newSpans, SnapshotSpan? newCurrentWord)
        {
            lock (updateLock)
            {
                if (currentRequest != RequestedPoint)
                    return;

                WordSpans = newSpans;
                CurrentWord = newCurrentWord;

                var tempEvent = TagsChanged;
                if (tempEvent != null)
                    tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }
        }

        #endregion Event Handlers

        #region ITagger<HighlightWordTag> Members

        public IEnumerable<ITagSpan<HighlightActionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (CurrentWord == null)
                yield break;

            // Hold on to a "snapshot" of the word spans and current word, so that we maintain the same
            // collection throughout
            SnapshotSpan currentWord = CurrentWord.Value;
            NormalizedSnapshotSpanCollection wordSpans = WordSpans;

            if (spans.Count == 0 || WordSpans.Count == 0)
                yield break;

            // If the requested snapshot isn't the same as the one our words are on, translate our spans
            // to the expected snapshot
            if (spans[0].Snapshot != wordSpans[0].Snapshot)
            {
                wordSpans = new NormalizedSnapshotSpanCollection(
                    wordSpans.Select(span => span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive)));

                currentWord = currentWord.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
            }

            // First, yield back the word the cursor is under (if it overlaps)
            // Note that we'll yield back the same word again in the wordspans collection;
            // the duplication here is expected.
            if (spans.OverlapsWith(new NormalizedSnapshotSpanCollection(currentWord)))
                yield return new TagSpan<HighlightActionTag>(currentWord, new HighlightActionTag());

            // Second, yield all the other words in the file
            foreach (SnapshotSpan span in NormalizedSnapshotSpanCollection.Overlap(spans, wordSpans))
            {
                yield return new TagSpan<HighlightActionTag>(span, new HighlightActionTag());
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #endregion ITagger<HighlightWordTag> Members
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("vsdscript")]
    [TagType(typeof(HighlightActionTag))]
    public class HighlightActionTaggerProvider : IViewTaggerProvider
    {
        #region ITaggerProvider Members

        [Import]
        internal ITextSearchService TextSearchService { get; set; }

        [Import]
        internal ITextStructureNavigatorSelectorService TextStructureNavigatorSelector { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            // Only provide highlighting on the top-level buffer
            if (!buffer.ContentType.IsOfType("vsdscript"))
                return null;

            ITextStructureNavigator textStructureNavigator =
                TextStructureNavigatorSelector.GetTextStructureNavigator(buffer);

            return new HighlightActionTagger(textView, buffer, TextSearchService, textStructureNavigator) as ITagger<T>;
        }

        #endregion ITaggerProvider Members
    }
}
