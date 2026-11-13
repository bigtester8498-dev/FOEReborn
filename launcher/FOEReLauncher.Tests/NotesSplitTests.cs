using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace FOEReLauncher.Tests
{
    public class NotesSplitTests
    {
        private static IList<PatchNote> Notes(params string[] versions) =>
            versions.Select(v => new PatchNote(v, "2026-11-13", new List<string> { "ru " + v }, new List<string> { "en " + v })).ToList();

        private static string[] Versions(IEnumerable<PatchNote> notes) => notes.Select(n => n.Version).ToArray();

        [Fact]
        public void Split_AtInstalledVersion()
        {
            var split = NotesSplit.Split(Notes("0.1.592", "0.1.589", "0.1.588", "0.1.586"), "0.1.588");
            Assert.True(split.HasInstalled);
            Assert.Equal(new[] { "0.1.592", "0.1.589" }, Versions(split.New));
            Assert.Equal(new[] { "0.1.588", "0.1.586" }, Versions(split.Old));
        }

        [Fact]
        public void Split_ComparesNumbersNotText()
        {
            // As text "0.1.1000" sorts before "0.1.999"; as numbers it is newer.
            var split = NotesSplit.Split(Notes("0.1.1000", "0.1.999", "0.1.98"), "0.1.999");
            Assert.Equal(new[] { "0.1.1000" }, Versions(split.New));
            Assert.Equal(new[] { "0.1.999", "0.1.98" }, Versions(split.Old));
        }

        [Fact]
        public void Split_AcceptsLeadingV()
        {
            var split = NotesSplit.Split(Notes("v0.1.592", "v0.1.589"), "0.1.589");
            Assert.Equal(new[] { "v0.1.592" }, Versions(split.New));
            Assert.Equal(new[] { "v0.1.589" }, Versions(split.Old));
        }

        [Fact]
        public void Split_NothingInstalled_EverythingIsNew()
        {
            foreach (var installed in new[] { null, "", "  " })
            {
                var split = NotesSplit.Split(Notes("0.1.592", "0.1.589"), installed);
                Assert.False(split.HasInstalled);
                Assert.Equal(new[] { "0.1.592", "0.1.589" }, Versions(split.New));
                Assert.Empty(split.Old);
            }
        }

        [Fact]
        public void Split_InstalledMissingFromList_UsesNumericOrder()
        {
            var split = NotesSplit.Split(Notes("0.1.592", "0.1.589", "0.1.586"), "0.1.587");
            Assert.Equal(new[] { "0.1.592", "0.1.589" }, Versions(split.New));
            Assert.Equal(new[] { "0.1.586" }, Versions(split.Old));
        }

        [Fact]
        public void Split_InstalledIsLatest_NoNewBlock()
        {
            var split = NotesSplit.Split(Notes("0.1.592", "0.1.589"), "0.1.592");
            Assert.Empty(split.New);
            Assert.Equal(2, split.Old.Count);
        }

        [Fact]
        public void Split_InstalledFromOtherChannel()
        {
            // A testing build newer than every release note: nothing is waiting.
            var newer = NotesSplit.Split(Notes("0.1.541", "0.1.530"), "0.1.592");
            Assert.Empty(newer.New);
            Assert.Equal(2, newer.Old.Count);

            // A release build older than every testing note: everything is waiting.
            var older = NotesSplit.Split(Notes("0.1.592", "0.1.589"), "0.1.541");
            Assert.Equal(2, older.New.Count);
            Assert.Empty(older.Old);
        }

        [Fact]
        public void Split_UnparsableVersions_FallBackToListPosition()
        {
            var split = NotesSplit.Split(Notes("next", "0.1.592", "build-a", "0.1.589"), "build-a");
            Assert.Equal(new[] { "next", "0.1.592" }, Versions(split.New));
            Assert.Equal(new[] { "build-a", "0.1.589" }, Versions(split.Old));
        }

        [Fact]
        public void Render_NewBlockThenDividerThenInstalled()
        {
            var pieces = PatchNotesForm.Render(NotesSplit.Split(Notes("0.1.592", "0.1.589", "0.1.588"), "0.1.588"));
            var kinds = pieces.Where(p => p.Key != PatchNotesForm.PieceKind.Empty && p.Key != PatchNotesForm.PieceKind.Line).Select(p => p.Key).ToArray();
            Assert.Equal(
                new[]
                {
                    PatchNotesForm.PieceKind.NewHeading,
                    PatchNotesForm.PieceKind.NewVersion,
                    PatchNotesForm.PieceKind.NewVersion,
                    PatchNotesForm.PieceKind.Divider,
                    PatchNotesForm.PieceKind.Version
                },
                kinds);
            Assert.Contains("2", pieces.First(p => p.Key == PatchNotesForm.PieceKind.NewHeading).Value);
            Assert.Contains("0.1.588", pieces.First(p => p.Key == PatchNotesForm.PieceKind.Divider).Value);
            Assert.StartsWith("0.1.592", pieces.First(p => p.Key == PatchNotesForm.PieceKind.NewVersion).Value);
        }

        [Fact]
        public void Render_NoInstall_NoHeadingNoDivider()
        {
            var pieces = PatchNotesForm.Render(NotesSplit.Split(Notes("0.1.592", "0.1.589"), null));
            Assert.DoesNotContain(pieces, p => p.Key == PatchNotesForm.PieceKind.NewHeading || p.Key == PatchNotesForm.PieceKind.Divider);
            Assert.Equal(2, pieces.Count(p => p.Key == PatchNotesForm.PieceKind.Version));
        }

        [Fact]
        public void Render_InstalledIsLatest_NoHeadingNoDivider()
        {
            var pieces = PatchNotesForm.Render(NotesSplit.Split(Notes("0.1.592", "0.1.589"), "0.1.592"));
            Assert.DoesNotContain(pieces, p => p.Key == PatchNotesForm.PieceKind.NewHeading || p.Key == PatchNotesForm.PieceKind.Divider);
            Assert.Equal(2, pieces.Count(p => p.Key == PatchNotesForm.PieceKind.Version));
        }

        [Fact]
        public void Render_EmptyList_SaysNothingPublished()
        {
            var pieces = PatchNotesForm.Render(NotesSplit.Split(new List<PatchNote>(), "0.1.592"));
            Assert.Single(pieces);
            Assert.Equal(PatchNotesForm.PieceKind.Line, pieces[0].Key);
        }
    }
}
