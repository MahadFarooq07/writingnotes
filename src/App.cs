using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PasteHandwriting
{
    public static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTests.Run();
            try
            {
                var app = new Application();
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, "PasteHandwriting", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true;
                };
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                {
                    var window = (Window)XamlReader.Load(stream);
                    new MainController(window);
                    app.Run(window);
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(ProfileStore.Root, "startup-error.txt"), ex.ToString());
                MessageBox.Show(ex.Message, "PasteHandwriting could not start", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
            }
        }
    }
    public sealed class MainController
    {
        readonly Window window;
        readonly ComboBox profiles;
        readonly InkCanvas preview, capture, samples, test;
        readonly TextBox source, character;
        readonly Stack<StrokeCollection> undo = new Stack<StrokeCollection>(), redo = new Stack<StrokeCollection>();
        StrokeCollection lastPreview = new StrokeCollection();
        readonly List<WritingProfile> library = new List<WritingProfile>();
        readonly string sequence = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?;:'\"()-+/=";
        int seed = 1729;
        bool changingPreview;
        bool previewEdited;
        bool sourceChanged;
        bool suppressSettings;
        WritingProfile generatedProfile;
        GenerationOptions generatedOptions;
        string generatedText = "";
        LetterSample lastSaved;
        WritingProfile lastSavedProfile;
        WritingProfile Active { get { return profiles.SelectedItem as WritingProfile; } }
        T Find<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        void Button(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        void Guard(Action action) { try { action(); } catch (Exception ex) { Text("Status", ex.Message); MessageBox.Show(window, ex.Message, "PasteHandwriting", MessageBoxButton.OK, MessageBoxImage.Information); } }

        public MainController(Window w)
        {
            window = w; profiles = Find<ComboBox>("Profiles"); preview = Find<InkCanvas>("Preview"); capture = Find<InkCanvas>("CaptureCanvas"); samples = Find<InkCanvas>("SamplePreview"); test = Find<InkCanvas>("TestCanvas"); source = Find<TextBox>("SourceText"); character = Find<TextBox>("CaptureCharacter");
            capture.DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.Black, Width = 2, Height = 2, IgnorePressure = false, FitToCurve = false };
            test.Strokes = InkEngine.TestStrokes();
            library.Add(DemoProfile.Create()); library.AddRange(ProfileStore.Load());
            profiles.ItemsSource = library;
            profiles.SelectionChanged += delegate { UpdateProfile(); sourceChanged = true; };
            character.TextChanged += delegate { UpdateCapture(); };
            source.TextChanged += delegate { ChangedSource(); };
            foreach (string name in new[] { "LetterSize", "PageWidth", "Variation", "PenWidth", "LetterSpacing" }) Find<Slider>(name).ValueChanged += delegate { Labels(); if (!suppressSettings) ChangedSource(); };
            Find<ComboBox>("InkColor").SelectionChanged += delegate { if (!suppressSettings) ChangedSource(); };
            Find<ComboBox>("PreviewMode").SelectionChanged += delegate
            {
                int mode = Find<ComboBox>("PreviewMode").SelectedIndex;
                preview.EditingMode = mode == 1 ? InkCanvasEditingMode.Select : mode == 2 ? InkCanvasEditingMode.EraseByStroke : mode == 3 ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.None;
            };
            preview.Strokes.StrokesChanged += PreviewChanged;
            preview.SelectionMoved += delegate { RememberEdit(); };
            preview.SelectionResized += delegate { RememberEdit(); };
            window.PreviewKeyDown += OnKey;
            Button("Generate", Generate);
            Button("NewVariation", delegate { seed = unchecked(seed + 7919); Generate(); });
            Button("CopyInk", delegate
            {
                if (sourceChanged) throw new InvalidOperationException("Your text, profile, or settings changed. Select Generate ink before copying.");
                Copy(preview.Strokes, "Ink copied. In OneNote, click the destination and press Ctrl+V.");
            });
            Button("CopyTest", delegate { Copy(InkEngine.TestStrokes(), "Six test strokes copied. Paste into a scratch page in OneNote."); });
            Button("ReadTest", ReadCopiedInk);
            Button("CreateProfile", CreateProfile);
            Button("SaveSample", SaveSample);
            Button("ClearSample", delegate { capture.Strokes.Clear(); });
            Button("UndoSample", delegate { if (capture.Strokes.Count > 0) capture.Strokes.RemoveAt(capture.Strokes.Count - 1); });
            Button("RemoveSample", UndoSavedSample);
            Button("PreviousCharacter", delegate { StepCharacter(-1); });
            Button("NextCharacter", delegate { StepCharacter(1); });
            Button("ExportProfile", ExportProfile);
            Button("ImportProfile", ImportProfile);
            Button("SaveDocument", SaveDocument);
            Button("LoadDocument", LoadDocument);
            Button("ExportPng", ExportPng);
            profiles.SelectedIndex = 0;
            Labels(); Generate(); UpdateCapture();
            Text("TestNotice", "Ready to copy 6 independent strokes. OneNote compatibility must be verified on your installation.");
            if (ProfileStore.LoadWarnings.Count > 0) Text("Status", "Some profiles could not be loaded; their files were preserved: " + string.Join("; ", ProfileStore.LoadWarnings));
        }
        void ChangedSource()
        {
            sourceChanged = true;
            Text("Status", "Text or settings changed. Select Generate ink to update the preview.");
        }
        void Labels()
        {
            Text("SizeLabel", Find<Slider>("LetterSize").Value.ToString("0") + " ink units");
            Text("WidthLabel", Find<Slider>("PageWidth").Value.ToString("0") + " ink units");
            Text("VariationLabel", (Find<Slider>("Variation").Value * 100).ToString("0") + "%");
            Text("PenLabel", Find<Slider>("PenWidth").Value.ToString("0.0") + " ink units");
        }
        GenerationOptions Options()
        {
            return new GenerationOptions { Size = Find<Slider>("LetterSize").Value, PageWidth = Find<Slider>("PageWidth").Value, Variation = Find<Slider>("Variation").Value, PenWidth = Find<Slider>("PenWidth").Value, LetterSpacing = Find<Slider>("LetterSpacing").Value, Seed = seed, Color = Find<ComboBox>("InkColor").SelectedIndex == 1 ? "#202020" : Find<ComboBox>("InkColor").SelectedIndex == 2 ? "#234C87" : "#244936" };
        }
        void Generate()
        {
            if (Active == null) return;
            var options = Options(); var result = InkEngine.Generate(Active, source.Text, options);
            SetPreview(result.Strokes, result.Width, result.Height);
            generatedProfile = Active; generatedOptions = options; generatedText = source.Text;
            sourceChanged = false; previewEdited = false;
            Text("Status", result.Letters + " characters · " + result.Strokes.Count + " independent strokes · " + result.Lines + " lines · variation " + seed);
            Text("ComposeNotice", Active.IsDemo ? "Demo letterforms are for trying the workflow. Create your own profile in Your handwriting to use your style." : "Generated from your saved samples. Select or erase ink in the preview; Ctrl+Z undoes preview edits.");
        }
        void SetPreview(StrokeCollection strokes, double width, double height)
        {
            changingPreview = true;
            preview.Strokes.StrokesChanged -= PreviewChanged;
            preview.Strokes = strokes;
            preview.Strokes.StrokesChanged += PreviewChanged;
            preview.Width = width; preview.Height = height;
            lastPreview = strokes.Clone(); undo.Clear(); redo.Clear(); changingPreview = false;
        }
        void PreviewChanged(object sender, StrokeCollectionChangedEventArgs e) { RememberEdit(); }
        void RememberEdit()
        {
            if (changingPreview) return;
            // Keep the newest snapshots within a small memory budget.
            if (undo.Count >= 20)
            {
                var recent = undo.Take(19).Reverse().ToArray(); undo.Clear(); foreach (var item in recent) undo.Push(item);
            }
            undo.Push(lastPreview); redo.Clear(); lastPreview = preview.Strokes.Clone(); previewEdited = true;
            Text("Status", "Preview edited · " + preview.Strokes.Count + " strokes · Ctrl+Z to undo. Generating replaces these preview edits.");
        }
        void OnKey(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Z || e.Key == Key.Y)
            {
                var from = e.Key == Key.Z ? undo : redo; var to = e.Key == Key.Z ? redo : undo;
                if (from.Count == 0) return;
                changingPreview = true; to.Push(preview.Strokes.Clone());
                preview.Strokes.StrokesChanged -= PreviewChanged; preview.Strokes = from.Pop(); preview.Strokes.StrokesChanged += PreviewChanged;
                lastPreview = preview.Strokes.Clone(); changingPreview = false; e.Handled = true;
                Text("Status", "Preview edit " + (e.Key == Key.Z ? "undone" : "restored") + " · " + preview.Strokes.Count + " strokes");
            }
        }
        void UpdateProfile()
        {
            if (Active == null) return;
            Text("ProfileSummary", Active.IsDemo ? "Try the demo, then capture your own writing" : Active.Samples.Select(s => s.Character).Distinct().Count() + " characters learned · " + Active.Samples.Count + " samples");
            Find<Button>("SaveSample").IsEnabled = !Active.IsDemo;
            Find<Button>("RemoveSample").IsEnabled = lastSaved != null && lastSavedProfile == Active;
            Text("CaptureNotice", Active.IsDemo ? "Create a personal profile using the panel on the left before saving your writing." : "Samples save automatically to this app's Data folder. Back up your profile to keep another copy.");
            UpdateCapture();
        }
        void UpdateCapture()
        {
            if (Active == null) return;
            string label = character.Text;
            var entries = Active.Samples.Where(s => s.Character == label).ToList();
            Text("CapturePrompt", "Write: " + label);
            Text("CaptureProgress", entries.Count + " saved examples of " + label + (entries.Count >= 3 ? " · ready" : " · aim for 3"));
            Text("Coverage", "Learned: " + string.Join(" ", Active.Samples.Select(s => s.Character).Distinct().OrderBy(c => c)));
            samples.Strokes = new StrokeCollection();
            double x = 20;
            foreach (var entry in entries.Take(20))
            {
                foreach (var path in entry.Paths)
                {
                    var points = new StylusPointCollection(path.Points.Select(p => new StylusPoint(x + p.X * 36, 65 + p.Y * 36, p.Pressure)));
                    samples.Strokes.Add(new Stroke(points) { DrawingAttributes = new DrawingAttributes { Width = 1.8, Height = 1.8, Color = Colors.DarkSlateGray, IgnorePressure = false } });
                }
                x += entry.Width * 36 + 30;
            }
            samples.Width = Math.Max(620, x + 20);
        }
        void CreateProfile()
        {
            string name = Find<TextBox>("ProfileName").Text.Trim();
            if (name.Length == 0 || name.Length > 80) throw new InvalidOperationException("Enter a profile name between 1 and 80 characters.");
            var p = new WritingProfile { Name = name }; ProfileStore.Save(p); library.Add(p); RefreshProfiles(p);
            capture.Strokes.Clear(); character.Text = "a";
            Text("Status", "Profile created. Write a on the guide and select Save sample.");
        }
        void RefreshProfiles(WritingProfile select) { profiles.ItemsSource = null; profiles.ItemsSource = library; profiles.SelectedItem = select; }
        void SaveSample()
        {
            if (Active.IsDemo) throw new InvalidOperationException("Create a personal profile first.");
            var sample = InkEngine.Capture(character.Text, capture.Strokes);
            Active.Samples.Add(sample);
            try { ProfileStore.Save(Active); } catch { Active.Samples.Remove(sample); throw; }
            lastSaved = sample; lastSavedProfile = Active; capture.Strokes.Clear(); UpdateProfile(); sourceChanged = true;
            Text("Status", "Saved sample for " + sample.Character + ". Write another example, or choose Next.");
        }
        void UndoSavedSample()
        {
            if (lastSaved == null || lastSavedProfile != Active) return;
            int index = Active.Samples.IndexOf(lastSaved);
            if (index < 0) return;
            Active.Samples.RemoveAt(index);
            try { ProfileStore.Save(Active); } catch { Active.Samples.Insert(index, lastSaved); throw; }
            lastSaved = null; UpdateProfile(); sourceChanged = true;
            Text("Status", "Last saved sample removed. The previous profile version is retained as a .bak file.");
        }
        void StepCharacter(int direction)
        {
            if (capture.Strokes.Count > 0) throw new InvalidOperationException("Save or clear the current writing before changing characters.");
            int current = sequence.IndexOf(character.Text);
            character.Text = sequence[(current + direction + sequence.Length) % sequence.Length].ToString();
        }
        void ExportProfile()
        {
            if (Active.IsDemo) throw new InvalidOperationException("Create a personal profile to back up your handwriting.");
            var dialog = new SaveFileDialog { Filter = "Handwriting profile (*.json)|*.json", FileName = "handwriting-profile.json" };
            if (dialog.ShowDialog(window) == true) { ProfileStore.Write(dialog.FileName, Active); Text("Status", "Profile backed up to " + dialog.FileName); }
        }
        void ImportProfile()
        {
            var dialog = new OpenFileDialog { Filter = "Handwriting profile (*.json)|*.json" };
            if (dialog.ShowDialog(window) != true) return;
            var p = ProfileStore.Read<WritingProfile>(dialog.FileName); ProfileStore.Validate(p); p.Id = Guid.NewGuid().ToString("N");
            ProfileStore.Save(p); library.Add(p); RefreshProfiles(p); Text("Status", "Imported " + p.Name + ". Existing profiles were preserved.");
        }
        void SaveDocument()
        {
            if (sourceChanged) throw new InvalidOperationException("Generate the current text and settings before saving.");
            if (preview.Strokes.Count == 0) throw new InvalidOperationException("Generate some ink first.");
            var dialog = new SaveFileDialog { Filter = "Handwriting document (*.phdoc)|*.phdoc|Ink Serialized Format (*.isf)|*.isf", FileName = "handwritten-note.phdoc" };
            if (dialog.ShowDialog(window) != true) return;
            byte[] bytes = InkEngine.Serialize(preview.Strokes);
            if (Path.GetExtension(dialog.FileName).Equals(".isf", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(dialog.FileName, bytes);
            else ProfileStore.Write(dialog.FileName, new SavedDocument { Text = generatedText, ProfileName = generatedProfile == null ? "Imported ink" : generatedProfile.Name, Options = generatedOptions, InkBase64 = Convert.ToBase64String(bytes), Width = preview.Width, Height = preview.Height });
            Text("Status", "Saved editable ink to " + dialog.FileName);
        }
        void LoadDocument()
        {
            var dialog = new OpenFileDialog { Filter = "Handwriting documents (*.phdoc;*.isf)|*.phdoc;*.isf" };
            if (dialog.ShowDialog(window) != true) return;
            StrokeCollection strokes; double width, height;
            if (Path.GetExtension(dialog.FileName).Equals(".isf", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(dialog.FileName).Length > 24 * 1024 * 1024) throw new InvalidDataException("This ink file is too large.");
                strokes = InkEngine.Deserialize(File.ReadAllBytes(dialog.FileName)); var b = strokes.GetBounds();
                width = b.IsEmpty ? 620 : Math.Max(620, b.Right + 28); height = b.IsEmpty ? 220 : Math.Max(220, b.Bottom + 28);
                source.Text = ""; generatedText = ""; generatedOptions = Options(); generatedProfile = null;
                Text("ComposeNotice", "Opened existing ink. Copy and save preserve its strokes. Generating creates new ink from the text box.");
            }
            else
            {
                var doc = ProfileStore.Read<SavedDocument>(dialog.FileName);
                if (doc == null || doc.Version != 1 || doc.InkBase64 == null || !ProfileStore.Finite(doc.Width) || doc.Width < 1 || doc.Width > 10000 || !ProfileStore.Finite(doc.Height) || doc.Height < 1 || doc.Height > 200000) throw new InvalidDataException("This isn't a supported handwriting document.");
                InkEngine.ValidateOptions(doc.Options);
                strokes = InkEngine.Deserialize(Convert.FromBase64String(doc.InkBase64)); width = doc.Width; height = doc.Height;
                source.Text = doc.Text ?? ""; generatedText = source.Text; generatedOptions = doc.Options; seed = doc.Options.Seed;
                generatedProfile = library.FirstOrDefault(p => p.Name == doc.ProfileName);
                if (generatedProfile != null) profiles.SelectedItem = generatedProfile;
                suppressSettings = true;
                Find<Slider>("LetterSize").Value = doc.Options.Size; Find<Slider>("PageWidth").Value = doc.Options.PageWidth; Find<Slider>("Variation").Value = doc.Options.Variation; Find<Slider>("PenWidth").Value = doc.Options.PenWidth; Find<Slider>("LetterSpacing").Value = doc.Options.LetterSpacing;
                Find<ComboBox>("InkColor").SelectedIndex = doc.Options.Color == "#202020" ? 1 : doc.Options.Color == "#234C87" ? 2 : 0;
                suppressSettings = false;
                Text("ComposeNotice", "Opened saved ink from " + doc.ProfileName + ". Preview edits were preserved." + (generatedProfile == null ? " Import its profile to regenerate in the same style." : ""));
            }
            if (width > 10000 || height > 200000) throw new InvalidDataException("The ink canvas is outside the supported size.");
            SetPreview(strokes, width, height); sourceChanged = false; previewEdited = true;
            Text("Status", "Opened " + strokes.Count + " editable strokes.");
        }
        void ExportPng()
        {
            if (preview.Strokes.Count == 0) throw new InvalidOperationException("Generate some ink first.");
            if (preview.Width * preview.Height > 25000000) throw new InvalidOperationException("This image would be very large. Save editable ink, or export a shorter passage.");
            var dialog = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "handwritten-note.png" };
            if (dialog.ShowDialog(window) != true) return;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) { context.DrawRectangle(Brushes.White, null, new Rect(0, 0, preview.Width, preview.Height)); preview.Strokes.Draw(context); }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(preview.Width * 2), (int)Math.Ceiling(preview.Height * 2), 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
            Text("Status", "PNG exported. Use Copy ink for editable OneNote strokes.");
        }
        void Copy(StrokeCollection strokes, string message)
        {
            if (strokes.Count == 0) throw new InvalidOperationException("There is no ink to copy.");
            var stream = new MemoryStream(InkEngine.Serialize(strokes));
            var data = new DataObject(); data.SetData(StrokeCollection.InkSerializedFormat, stream, false);
            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            Action attempt = delegate
            {
                try
                {
                    stream.Position = 0; Clipboard.SetDataObject(data, true); timer.Stop(); stream.Dispose(); Text("Status", message);
                    Text("TestNotice", "Copied ink using the native ISF clipboard format. Verify its behavior in OneNote; appearance alone is not proof of editability.");
                }
                catch (ExternalException)
                {
                    if (++attempts >= 5) { timer.Stop(); stream.Dispose(); Text("Status", "The clipboard is busy. Try Copy ink again."); }
                    else timer.Start();
                }
                catch (Exception ex) { timer.Stop(); stream.Dispose(); Text("Status", "Could not copy ink: " + ex.Message); }
            };
            timer.Tick += delegate { attempt(); }; attempt();
        }
        void ReadCopiedInk()
        {
            var data = Clipboard.GetDataObject();
            if (data == null || !data.GetDataPresent(StrokeCollection.InkSerializedFormat, false))
            {
                Text("TestNotice", "The clipboard does not expose ISF ink. Copy a lasso-selected ink sample in OneNote and try again. This result alone does not establish whether the pasted sample is editable."); return;
            }
            var value = data.GetData(StrokeCollection.InkSerializedFormat, false); byte[] bytes;
            var stream = value as Stream;
            if (stream != null)
            {
                if (stream.CanSeek) { if (stream.Length > 24 * 1024 * 1024) throw new InvalidDataException("Clipboard ink is too large."); stream.Position = 0; }
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { output.Write(buffer, 0, read); if (output.Length > 24 * 1024 * 1024) throw new InvalidDataException("Clipboard ink is too large."); }
                    bytes = output.ToArray();
                }
            }
            else { bytes = value as byte[]; if (bytes == null) throw new InvalidDataException("The clipboard uses an unsupported ink representation."); }
            var strokes = InkEngine.Deserialize(bytes); test.Strokes = strokes;
            var bounds = strokes.GetBounds();
            if (!bounds.IsEmpty)
            {
                var matrix = Matrix.Identity; matrix.Translate(-bounds.X + 20, -bounds.Y + 20); strokes.Transform(matrix, false);
                double scale = Math.Min(1, Math.Min(550 / Math.Max(1, bounds.Width), 230 / Math.Max(1, bounds.Height)));
                if (scale < 1) { var shrink = Matrix.Identity; shrink.Scale(scale, scale); strokes.Transform(shrink, true); }
            }
            Text("TestNotice", "Read " + strokes.Count + " editable strokes · " + strokes.Sum(s => s.StylusPoints.Count) + " pen points from the clipboard. The test preview is scaled to fit.");
            Text("Status", "Clipboard ink decoded successfully.");
        }
    }
}
