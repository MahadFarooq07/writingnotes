using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Ink;
using System.Windows.Input;

namespace PasteHandwriting
{
    public static class SelfTests
    {
        static readonly List<string> lines = new List<string>();
        static void Check(bool condition, string name) { if (!condition) throw new Exception(name); lines.Add("PASS " + name); }
        static string Signature(StrokeCollection strokes)
        {
            return string.Join("|", strokes.Select(s => string.Join(";", s.StylusPoints.Select(p => p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.PressureFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));
        }
        static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        public static int Run()
        {
            int code = 0;
            try
            {
                var p = DemoProfile.Create(); ProfileStore.Validate(p);
                string ascii = new string(Enumerable.Range(33, 94).Select(i => (char)i).ToArray());
                Check(InkEngine.Missing(p, ascii) == "", "demo covers every printable ASCII character");
                var o = new GenerationOptions();
                var first = InkEngine.Generate(p, "Hello, little world!\nA dotted i and a crossed t.", o);
                var second = InkEngine.Generate(p, "Hello, little world!\nA dotted i and a crossed t.", o);
                Check(Signature(first.Strokes) == Signature(second.Strokes), "same seed reproduces every pen point");
                o.Seed++; var different = InkEngine.Generate(p, "Hello, little world!\nA dotted i and a crossed t.", o);
                Check(Signature(first.Strokes) != Signature(different.Strokes), "different seed changes handwriting");
                o.Variation = 0; var plainA = InkEngine.Generate(p, "aaaa", o); o.Seed++; var plainB = InkEngine.Generate(p, "aaaa", o);
                Check(Signature(plainA.Strokes) == Signature(plainB.Strokes), "zero variation removes seeded changes");
                var dotted = InkEngine.Generate(p, "ii", o);
                Check(dotted.Strokes.Count == 4 && (int)dotted.Strokes[0].GetPropertyData(InkEngine.CharacterIndex) == 0 && (int)dotted.Strokes[2].GetPropertyData(InkEngine.CharacterIndex) == 1, "dotted letters preserve pen lifts and character ownership");
                dotted.Strokes.RemoveAt(0); Check(dotted.Strokes.Count == 3 && (string)dotted.Strokes[1].GetPropertyData(InkEngine.CharacterLabel) == "i", "removing one stroke preserves neighboring letter strokes");
                var decoded = InkEngine.Deserialize(InkEngine.Serialize(first.Strokes));
                Check(decoded.Count == first.Strokes.Count && decoded.Sum(s => s.StylusPoints.Count) == first.Strokes.Sum(s => s.StylusPoints.Count), "ISF round trip preserves stroke and point counts");
                Check(decoded.All(s => s.ContainsPropertyData(InkEngine.CharacterIndex)), "ISF preserves internal character metadata");
                Check(decoded.SelectMany(s => s.StylusPoints).Select(pt => pt.PressureFactor).Distinct().Count() > 5, "ISF preserves varying pressure");
                var crlf = InkEngine.Generate(p, "one\r\ntwo", o); var lf = InkEngine.Generate(p, "one\ntwo", o);
                Check(Signature(crlf.Strokes) == Signature(lf.Strokes), "Windows and Unix newlines lay out identically");
                var blank = InkEngine.Generate(p, "a\n\na", o); Check(blank.Lines == 3, "blank lines are preserved");
                o.PageWidth = 280;
                var wrapped = InkEngine.Generate(p, string.Join(" ", Enumerable.Repeat("notebook", 50)), o);
                Check(wrapped.Lines > 1 && wrapped.Strokes.GetBounds().Right < o.PageWidth, "long text wraps within requested width");
                var wide = InkEngine.Generate(p, new string('W', 100), o);
                Check(wide.Lines > 1 && wide.Strokes.GetBounds().Right < o.PageWidth, "oversized words wrap by character");
                Check(Throws(delegate { InkEngine.Generate(p, "snowman \u2603", o); }), "unsupported characters are rejected without silent omission");
                Check(Throws(delegate { InkEngine.Generate(p, new string('a', 10001), o); }), "text size limit is enforced");
                var test = InkEngine.TestStrokes(); Check(test.Count == 6, "clipboard test contains six separate strokes");
                var ink = new StrokeCollection(); ink.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(80, 100, .4f), new StylusPoint(100, 180, .8f) }))); ink.Add(new Stroke(new StylusPointCollection(new[] { new StylusPoint(90, 70, .5f) })));
                var sample = InkEngine.Capture("i", ink);
                Check(sample.Paths.Count == 2 && sample.Paths[0].Points[1].Y == 0 && sample.RawPaths[0].Points[0].X == 80, "sample capture preserves raw ink and baseline normalization");
                var user = new WritingProfile { Name = "Test profile" }; user.Samples.Add(sample);
                Check(InkEngine.Missing(user, "ii \n") == "" && InkEngine.Missing(user, "ia") == "a", "personal profiles report exact missing characters");
                var path = Path.Combine(ProfileStore.Root, "test-profile.json");
                ProfileStore.Write(path, user); var loaded = ProfileStore.Read<WritingProfile>(path); ProfileStore.Validate(loaded);
                Check(loaded.Name == user.Name && loaded.Samples[0].RawPaths.Count == 2, "profile JSON round trip preserves captured data");
                loaded.Id = "..\\escape"; Check(Throws(delegate { ProfileStore.Validate(loaded); }), "profile IDs cannot traverse storage paths");
                loaded.Id = Guid.NewGuid().ToString("N"); loaded.Samples[0].Paths[0].Points[0].X = double.NaN;
                Check(Throws(delegate { ProfileStore.Validate(loaded); }), "invalid imported coordinates are rejected");
                File.Delete(path);
                var document = new SavedDocument { Text = "saved text", ProfileName = p.Name, Options = o, InkBase64 = Convert.ToBase64String(InkEngine.Serialize(first.Strokes)), Width = first.Width, Height = first.Height };
                var docPath = Path.Combine(ProfileStore.Root, "test-document.phdoc");
                ProfileStore.Write(docPath, document); var doc = ProfileStore.Read<SavedDocument>(docPath);
                Check(doc.Text == "saved text" && InkEngine.Deserialize(Convert.FromBase64String(doc.InkBase64)).Count == first.Strokes.Count, "saved documents retain source text and editable ink"); File.Delete(docPath);
                lines.Add("All " + lines.Count + " checks passed. OneNote UI compatibility is a separate manual test.");
            }
            catch (Exception ex) { lines.Add("FAIL " + ex); code = 1; }
            File.WriteAllLines(Path.Combine(ProfileStore.Root, "test-results.txt"), lines);
            return code;
        }
    }
}
