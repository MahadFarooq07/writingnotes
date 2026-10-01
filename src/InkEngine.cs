using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace PasteHandwriting
{
    [DataContract] public sealed class InkPoint
    {
        [DataMember] public double X;
        [DataMember] public double Y;
        [DataMember] public float Pressure;
        public InkPoint() { }
        public InkPoint(double x, double y, float p) { X = x; Y = y; Pressure = p; }
    }
    [DataContract] public sealed class PenPath
    {
        [DataMember] public List<InkPoint> Points = new List<InkPoint>();
    }
    [DataContract] public sealed class LetterSample
    {
        [DataMember] public string Character;
        [DataMember] public List<PenPath> Paths = new List<PenPath>();
        [DataMember] public double Width;
        [DataMember] public string CapturedUtc;
        [DataMember] public List<PenPath> RawPaths = new List<PenPath>();
    }
    [DataContract] public sealed class WritingProfile
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name;
        [DataMember] public List<LetterSample> Samples = new List<LetterSample>();
        [IgnoreDataMember] public bool IsDemo;
        public override string ToString() { return Name; }
    }
    [DataContract] public sealed class GenerationOptions
    {
        [DataMember] public double Size = 22;
        [DataMember] public double PageWidth = 620;
        [DataMember] public double Variation = .35;
        [DataMember] public double PenWidth = 1.6;
        [DataMember] public double LetterSpacing = .12;
        [DataMember] public int Seed = 1729;
        [DataMember] public string Color = "#244936";
    }
    [DataContract] public sealed class SavedDocument
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Text;
        [DataMember] public string ProfileName;
        [DataMember] public GenerationOptions Options;
        [DataMember] public string InkBase64;
        [DataMember] public double Width;
        [DataMember] public double Height;
    }
    public sealed class GeneratedInk
    {
        public StrokeCollection Strokes = new StrokeCollection();
        public double Width;
        public double Height;
        public int Letters;
        public int Lines;
    }
    public static class ProfileStore
    {
        public static readonly string Root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        public static readonly string DirectoryPath = Path.Combine(Root, "Data", "Profiles");
        public static readonly List<string> LoadWarnings = new List<string>();
        public static T Read<T>(string path)
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("This file is too large (32 MB limit).");
            using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static void Write<T>(string path, T data)
        {
            string temp = path + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) new DataContractJsonSerializer(typeof(T)).WriteObject(stream, data);
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void Save(WritingProfile profile)
        {
            if (profile.IsDemo) throw new InvalidOperationException("Create your own profile before saving samples.");
            Validate(profile);
            Directory.CreateDirectory(DirectoryPath);
            Write(Path.Combine(DirectoryPath, profile.Id + ".json"), profile);
        }
        public static List<WritingProfile> Load()
        {
            Directory.CreateDirectory(DirectoryPath);
            LoadWarnings.Clear();
            var result = new List<WritingProfile>();
            foreach (string path in Directory.GetFiles(DirectoryPath, "*.json"))
            {
                try { var profile = Read<WritingProfile>(path); Validate(profile); result.Add(profile); }
                catch (Exception ex) { LoadWarnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            return result.OrderBy(p => p.Name).ToList();
        }
        public static void Validate(WritingProfile profile)
        {
            Guid id;
            if (profile == null || profile.Version != 1 || !Guid.TryParse(profile.Id, out id) || id.ToString("N") != profile.Id || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Samples == null)
                throw new InvalidDataException("This isn't a supported handwriting profile.");
            int points = 0;
            if (profile.Samples.Count > 4000) throw new InvalidDataException("Too many samples in this profile.");
            foreach (var sample in profile.Samples)
            {
                if (sample == null || sample.Character == null || sample.Character.Length != 1 || sample.Character[0] < '!' || sample.Character[0] > '~' || !Finite(sample.Width) || sample.Width <= 0 || sample.Width > 12 || sample.Paths == null || sample.Paths.Count == 0 || sample.Paths.Count > 100)
                    throw new InvalidDataException("A letter sample is invalid.");
                foreach (var stroke in sample.Paths)
                {
                    if (stroke == null || stroke.Points == null || stroke.Points.Count < 1) throw new InvalidDataException("An ink path is empty.");
                    foreach (var point in stroke.Points)
                    {
                        if (point == null || !Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 20 || Math.Abs(point.Y) > 20 || float.IsNaN(point.Pressure) || point.Pressure < 0 || point.Pressure > 1)
                            throw new InvalidDataException("An ink point is outside the supported range.");
                        if (++points > 1000000) throw new InvalidDataException("Too many points in this profile.");
                    }
                }
            }
        }
        public static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    }
    public static class InkEngine
    {
        public static readonly Guid CharacterIndex = new Guid("ac8221d6-9f11-43f2-90cb-a7a1b9ec7312");
        public static readonly Guid CharacterLabel = new Guid("08bce519-fac1-4669-9dbc-6b5118b43d9f");
        static double Noise(Random random) { return random.NextDouble() * 2 - 1; }
        public static LetterSample Capture(string label, StrokeCollection strokes)
        {
            if (label == null || label.Length != 1 || label[0] < '!' || label[0] > '~') throw new InvalidOperationException("Choose one letter, number, or punctuation character.");
            if (strokes.Count == 0) throw new InvalidOperationException("Write the character on the canvas first.");
            if (strokes.Count > 100 || strokes.Sum(s => s.StylusPoints.Count) > 50000) throw new InvalidOperationException("This sample has too much ink. Capture one character at a time.");
            var all = strokes.SelectMany(s => s.StylusPoints).ToList();
            double left = all.Min(p => p.X), right = all.Max(p => p.X);
            var sample = new LetterSample { Character = label, Width = Math.Max(.07, (right - left) / 80), CapturedUtc = DateTime.UtcNow.ToString("o") };
            foreach (var stroke in strokes)
            {
                var path = new PenPath(); var raw = new PenPath();
                foreach (var p in stroke.StylusPoints)
                {
                    path.Points.Add(new InkPoint((p.X - left) / 80, (p.Y - 180) / 80, p.PressureFactor));
                    raw.Points.Add(new InkPoint(p.X, p.Y, p.PressureFactor));
                }
                sample.Paths.Add(path); sample.RawPaths.Add(raw);
            }
            return sample;
        }
        public static string Missing(WritingProfile profile, string text)
        {
            var available = new HashSet<char>(profile.Samples.Select(s => s.Character[0]));
            return new string((text ?? "").Where(c => c != ' ' && c != '\r' && c != '\n' && c != '\t' && !available.Contains(c)).Distinct().OrderBy(c => c).ToArray());
        }
        public static void ValidateOptions(GenerationOptions o)
        {
            if (o == null || !ProfileStore.Finite(o.Size) || o.Size < 8 || o.Size > 60 || !ProfileStore.Finite(o.PageWidth) || o.PageWidth < 200 || o.PageWidth > 1600 || !ProfileStore.Finite(o.Variation) || o.Variation < 0 || o.Variation > 1 || !ProfileStore.Finite(o.PenWidth) || o.PenWidth < .3 || o.PenWidth > 6 || !ProfileStore.Finite(o.LetterSpacing) || o.LetterSpacing < .01 || o.LetterSpacing > .6)
                throw new InvalidDataException("The writing settings are outside the supported range.");
            try { ColorConverter.ConvertFromString(o.Color); } catch { throw new InvalidDataException("The ink color is invalid."); }
        }
        public static GeneratedInk Generate(WritingProfile profile, string text, GenerationOptions o)
        {
            ProfileStore.Validate(profile); ValidateOptions(o);
            text = text ?? "";
            if (text.Length > 10000) throw new InvalidOperationException("Use up to 10,000 characters per passage.");
            string missing = Missing(profile, text);
            if (missing.Length > 0) throw new InvalidOperationException("This profile needs samples for: " + missing + ". Add them in Your handwriting, then generate again.");
            var groups = profile.Samples.GroupBy(s => s.Character[0]).ToDictionary(g => g.Key, g => g.ToList());
            var random = new Random(o.Seed); var previous = new Dictionary<char, int>();
            var result = new GeneratedInk { Width = o.PageWidth, Lines = 1 };
            double margin = 28, x = margin, baseline = margin + o.Size * 1.75, lineHeight = o.Size * 2.3;
            double lineTilt = Noise(random) * o.Variation * .008, lineOffset = Noise(random) * o.Variation * .08 * o.Size;
            double wordScale = 1, wordSlant = 0; bool startsWord = true;
            Color color = (Color)ColorConverter.ConvertFromString(o.Color);
            Action newline = delegate { x = margin; baseline += lineHeight; result.Lines++; lineTilt = Noise(random) * o.Variation * .008; lineOffset = Noise(random) * o.Variation * .08 * o.Size; startsWord = true; };
            long totalPoints = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; newline(); continue; }
                if (ch == '\n') { newline(); continue; }
                if (ch == ' ' || ch == '\t')
                {
                    x += o.Size * (ch == '\t' ? 2 : .5) * (1 + Noise(random) * o.Variation * .12);
                    if (x > o.PageWidth - margin) newline();
                    startsWord = true; continue;
                }
                if (startsWord)
                {
                    double estimate = 0;
                    for (int j = i; j < text.Length && text[j] != ' ' && text[j] != '\t' && text[j] != '\r' && text[j] != '\n'; j++) estimate += (groups[text[j]].Average(s => s.Width) + o.LetterSpacing) * o.Size;
                    if (x > margin && x + estimate > o.PageWidth - margin) newline();
                    wordScale = 1 + Noise(random) * o.Variation * .04; wordSlant = Noise(random) * o.Variation * .07; startsWord = false;
                }
                var choices = groups[ch]; int chosen = o.Variation == 0 ? 0 : random.Next(choices.Count);
                int prev;
                if (choices.Count > 1 && o.Variation > 0 && previous.TryGetValue(ch, out prev) && chosen == prev) chosen = (chosen + 1 + random.Next(choices.Count - 1)) % choices.Count;
                previous[ch] = chosen; var sample = choices[chosen];
                double sx = o.Size * wordScale * (1 + Noise(random) * o.Variation * .055);
                double sy = o.Size * wordScale * (1 + Noise(random) * o.Variation * .035);
                double advance = sample.Width * sx + o.Size * o.LetterSpacing * (1 + Noise(random) * o.Variation * .18);
                if (x > margin && x + advance > o.PageWidth - margin) newline();
                if (advance > o.PageWidth - 2 * margin) throw new InvalidOperationException("A sample is too wide for this line. Increase line width, reduce size, or recapture " + ch + ".");
                double offset = Noise(random) * o.Variation * .035 * o.Size;
                double slant = wordSlant + Noise(random) * o.Variation * .025;
                foreach (var path in sample.Paths)
                {
                    totalPoints += path.Points.Count;
                    if (totalPoints > 1500000) throw new InvalidOperationException("This passage contains too many ink points. Split it into shorter passages.");
                    var points = new StylusPointCollection();
                    double length = 0, walked = 0;
                    for (int k = 1; k < path.Points.Count; k++) length += Distance(path.Points[k - 1], path.Points[k]);
                    double phase = random.NextDouble() * Math.PI * 2;
                    double pressureGain = 1 + Noise(random) * o.Variation * .08;
                    for (int k = 0; k < path.Points.Count; k++)
                    {
                        var p = path.Points[k];
                        if (k > 0) walked += Distance(path.Points[k - 1], p);
                        double t = length > 0 ? walked / length : 0;
                        double wobble = Math.Sin(t * Math.PI * 2 + phase) * Math.Sin(t * Math.PI) * o.Variation * .009 * o.Size;
                        double px = x + p.X * sx + p.Y * sy * slant + wobble;
                        double py = baseline + lineOffset + (x - margin) * lineTilt + p.Y * sy + offset;
                        float pressure = (float)Math.Max(.02, Math.Min(1, p.Pressure * pressureGain));
                        points.Add(new StylusPoint(px, py, pressure));
                    }
                    var stroke = new Stroke(points);
                    stroke.DrawingAttributes = new DrawingAttributes { Color = color, Width = o.PenWidth, Height = o.PenWidth, FitToCurve = false, IgnorePressure = false };
                    stroke.AddPropertyData(CharacterIndex, i); stroke.AddPropertyData(CharacterLabel, ch.ToString());
                    result.Strokes.Add(stroke);
                }
                result.Letters++; x += advance;
            }
            var bounds = result.Strokes.GetBounds();
            result.Height = Math.Max(220, Math.Max(baseline + o.Size + margin, bounds.IsEmpty ? 0 : bounds.Bottom + margin));
            return result;
        }
        static double Distance(InkPoint a, InkPoint b) { return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }
        public static byte[] Serialize(StrokeCollection strokes)
        {
            using (var stream = new MemoryStream()) { strokes.Save(stream); return stream.ToArray(); }
        }
        public static StrokeCollection Deserialize(byte[] data)
        {
            if (data.Length > 24 * 1024 * 1024) throw new InvalidDataException("This ink file is too large.");
            using (var stream = new MemoryStream(data)) return new StrokeCollection(stream);
        }
        public static StrokeCollection TestStrokes()
        {
            var strokes = new StrokeCollection();
            double[][][] paths = {
                new[] { new[] {30.0,150.0}, new[] {120.0,150.0} },
                new[] { new[] {200.0,110.0}, new[] {175.0,95.0}, new[] {150.0,110.0}, new[] {145.0,140.0}, new[] {165.0,165.0}, new[] {200.0,155.0} },
                new[] { new[] {280.0,75.0}, new[] {280.0,155.0}, new[] {290.0,165.0}, new[] {305.0,155.0} },
                new[] { new[] {255.0,110.0}, new[] {305.0,110.0} },
                new[] { new[] {360.0,112.0}, new[] {360.0,162.0} },
                new[] { new[] {360.0,85.0}, new[] {360.1,85.1} }
            };
            for (int i = 0; i < paths.Length; i++)
            {
                var pts = new StylusPointCollection();
                for (int j = 0; j < paths[i].Length; j++) pts.Add(new StylusPoint(paths[i][j][0], paths[i][j][1], (float)(.35 + .5 * j / Math.Max(1, paths[i].Length - 1))));
                var stroke = new Stroke(pts) { DrawingAttributes = new DrawingAttributes { Width = 3, Height = 3, Color = Colors.DarkSlateGray, IgnorePressure = false } };
                strokes.Add(stroke);
            }
            return strokes;
        }
    }
}
