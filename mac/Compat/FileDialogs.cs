using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AltsTools.Compat
{
    // Minimal Open/Save dialog shims with the WPF-style API the ported code uses
    // (Filter / Title / FileName / DefaultExt + bool? ShowDialog()). Backed by
    // Avalonia's StorageProvider.
    public abstract class FileDialogBase
    {
        public string Filter { get; set; } = "";       // "PNG (*.png)|*.png|All|*.*"
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DefaultExt { get; set; } = "";

        protected static Avalonia.Controls.Window? Owner
            => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        // Parse a WPF filter string into Avalonia FilePickerFileType list.
        protected IReadOnlyList<FilePickerFileType> ParseFilters()
        {
            var types = new List<FilePickerFileType>();
            if (string.IsNullOrWhiteSpace(Filter)) return types;
            var parts = Filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string name = parts[i];
                var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(p => p.Trim()).ToList();
                types.Add(new FilePickerFileType(name) { Patterns = patterns });
            }
            return types;
        }
    }

    public sealed class OpenFileDialog : FileDialogBase
    {
        public bool? ShowDialog()
        {
            var owner = Owner;
            if (owner?.StorageProvider == null) return false;
            return Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Title,
                    AllowMultiple = false,
                    FileTypeFilter = ParseFilters(),
                });
                if (files.Count == 0) return (bool?)false;
                FileName = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                return true;
            }).GetAwaiter().GetResult();
        }
    }

    public sealed class SaveFileDialog : FileDialogBase
    {
        public bool? ShowDialog()
        {
            var owner = Owner;
            if (owner?.StorageProvider == null) return false;
            return Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Title,
                    SuggestedFileName = FileName,
                    DefaultExtension = DefaultExt.TrimStart('.'),
                    FileTypeChoices = ParseFilters(),
                });
                if (file == null) return (bool?)false;
                FileName = file.TryGetLocalPath() ?? file.Path.LocalPath;
                return true;
            }).GetAwaiter().GetResult();
        }
    }
}
