# PasteHandwriting

A Windows app that turns typed text into handwriting strokes you can copy into OneNote as editable ink. Capture your own letter samples with a stylus, save a handwriting profile, and generate passages with controlled variation.

## Run

Open `bin/PasteHandwriting.exe` on Windows with .NET Framework 4.8 installed. Keep `PasteHandwriting.exe.config` beside the executable. No Python, GPU, or cloud service is required.

The included demo profile lets you try the workflow before collecting samples. Its letterforms are synthetic and are clearly labeled as demo handwriting.

## Use your handwriting

1. Open **Your handwriting** and create a profile.
2. Write the displayed character with your stylus, using the baseline and height guides.
3. Select **Save sample**. Record several natural examples, ideally three or more per character.
4. Use **Next** to capture additional letters, numbers, and punctuation.
5. Open **Compose**, choose your profile, and paste your text.
6. Adjust size, spacing, pen width, color, and variation, then select **Generate ink**.
7. Select **Copy ink for OneNote**, click a destination in OneNote desktop, and press **Ctrl+V**.

Characters that have no samples in the selected profile are reported before generation. They are never silently omitted or replaced with demo letters.

Profiles save automatically in `Data/Profiles` beside the project directory. Use **Back up profile** and **Import profile** to transfer them. Personal sample files are excluded from Git.

## Features

- Guided capture of actual pen paths, pressure, and pen lifts.
- Multiple samples per character, with variation in shape, spacing, baseline, and pressure.
- Paragraph wrapping, explicit newlines, and reproducible variation seeds.
- Editable preview with selection, movement, stroke erasing, point erasing, and Ctrl+Z/Ctrl+Y.
- Native ISF ink clipboard output for OneNote.
- Editable `.phdoc` documents and `.isf` exports, plus optional PNG image export.
- A dedicated OneNote clipboard test and ink round-trip reader.

## Build and check

From PowerShell in the repository directory:

```powershell
.\build.ps1
.\build.ps1 -Test
```

The build uses the Windows .NET Framework C# compiler and WPF assemblies. It does not download dependencies. Output is written to `bin/`; the self-test report is written to `test-results.txt`.

## Validation and current limits

The initial build passed 22 checks covering reproducibility, character/stroke separation, pressure and ISF serialization, layout, unsupported-character handling, and profile/document persistence.

On the development machine, six generated strokes were pasted into OneNote desktop and copied back. The app decoded all six strokes and their 18 pen points. This verifies native ink transfer on that installation. OneNote eraser behavior and save/reopen persistence were not yet verified through the interface.

The first version uses recorded samples and bounded variation. Personal resemblance depends on the samples you capture; it does not use a trained neural handwriting model. Supported profile characters are printable ASCII letters, numbers, and punctuation. This is intended for mostly printed handwriting, with Windows desktop OneNote as the paste target.

A letter can contain multiple strokes, such as the stem and dot of an `i`. OneNote's stroke eraser operates on strokes, so a single tap is not guaranteed to erase every component of a letter.

See [RESEARCH_AND_PLAN.md](RESEARCH_AND_PLAN.md) for the research, architecture, alternatives, and remaining validation work.
