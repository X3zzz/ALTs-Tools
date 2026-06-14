// Mirrors the Windows project's GlobalAliases.cs: remap the names ported
// code expects onto the macOS/Avalonia compat shims so the ViewModels port
// with minimal edits.
global using MessageBox = AltsTools.Compat.MessageBox;
global using MessageBoxButton = AltsTools.Compat.MessageBoxButton;
global using MessageBoxImage = AltsTools.Compat.MessageBoxImage;
global using MessageBoxResult = AltsTools.Compat.MessageBoxResult;
global using Clipboard = AltsTools.Helpers.SafeClipboard;
global using OpenFileDialog = AltsTools.Compat.OpenFileDialog;
global using SaveFileDialog = AltsTools.Compat.SaveFileDialog;
