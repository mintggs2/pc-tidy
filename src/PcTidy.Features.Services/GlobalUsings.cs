// WPF projects drop System.IO from the implicit usings (Path clashes with System.Windows.Shapes.Path).
global using System.IO;
global using PcTidy.Catalog.Services;
global using PcTidy.Core;
global using PcTidy.Core.Model;
// Wpf.Ui.Controls also has a Badge control; in this project "Badge" always means the scan item's safety badge.
global using Badge = PcTidy.Core.Model.Badge;
