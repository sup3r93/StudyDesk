using System.Runtime.CompilerServices;

// Focused tests may drive native rejection paths; no mutable registration state is exposed to the renderer.
[assembly: InternalsVisibleTo("StudyDesk.Desktop.Ui.Tests")]
[assembly: InternalsVisibleTo("StudyDesk.Desktop.Runtime.Tests")]
