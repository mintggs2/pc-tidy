// WPF projects drop System.IO from the implicit usings (Path clashes with System.Windows.Shapes.Path).
// This library has no XAML, so bring it back.
global using System.IO;
