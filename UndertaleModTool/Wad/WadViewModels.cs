using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using UndertaleModLib.Wad;

namespace UndertaleModTool.Wad
{
    /// <summary>Minimal observable base used by the WAD editor view models.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>Simple <see cref="ICommand"/> implementation (the classic app has no toolkit).</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute is null || _canExecute();

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A labeled key/value pair shown in the chunk header area of the per-chunk views.</summary>
    public sealed class WadInfoItem
    {
        public WadInfoItem(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>One row of the reflected property readout of a selected entry.</summary>
    public sealed class WadPropertyViewModel
    {
        public WadPropertyViewModel(string name, string valueText)
        {
            Name = name;
            ValueText = valueText;
        }

        public string Name { get; }
        public string ValueText { get; }
    }

    /// <summary>One entry row shown in the entries grid of a chunk editor.</summary>
    public sealed class WadEntryViewModel
    {
        public WadEntryViewModel(int index, string name, string summary, object payload)
        {
            Index = index;
            Name = name;
            Summary = summary;
            Payload = payload;
        }

        public int Index { get; }
        public string Name { get; }
        public string Summary { get; }

        /// <summary>The underlying parsed entry object (used for the property readout).</summary>
        public object Payload { get; }
    }

    /// <summary>
    /// Root view model of the WAD editor: the open file, its chunk table, and the entry
    /// preview of the selected chunk. Chunks are opened in their own tabs through the main
    /// window's editor hosting (OpenInTab + DataTemplates).
    /// </summary>
    public sealed class WadFileViewModel : ObservableObject, IDisposable
    {
        private readonly RelayCommand _openCommand;
        private UndertaleWadFile _wad;

        public WadFileViewModel()
        {
            _openCommand = new RelayCommand(OpenFileDialog, () => true);
        }

        /// <summary>The chunk table of the current file.</summary>
        public ObservableCollection<WadChunkViewModel> Chunks { get; } = new();

        /// <summary>The entries preview for the selected chunk.</summary>
        public ObservableCollection<WadEntryViewModel> ChunkEntries { get; } = new();

        public ICommand OpenCommand => _openCommand;

        private WadChunkViewModel _selectedChunk;

        /// <summary>Selected chunk feeds the entry preview; double-click opens a dedicated tab.</summary>
        public WadChunkViewModel SelectedChunk
        {
            get => _selectedChunk;
            set
            {
                if (SetProperty(ref _selectedChunk, value))
                    RefreshChunkEntries(value);
            }
        }

        private string _filePath;
        private string _fileInfoText;

        public string FilePath
        {
            get => _filePath;
            private set => SetProperty(ref _filePath, value);
        }

        /// <summary>"FORM … · N chunks · M strings" summary label.</summary>
        public string FileInfoText
        {
            get => _fileInfoText;
            private set => SetProperty(ref _fileInfoText, value);
        }

        /// <summary>Loads the given file and rebuilds the chunk table.</summary>
        public void LoadFile(string path)
        {
            _wad?.Dispose();
            Chunks.Clear();
            ChunkEntries.Clear();

            _wad = UndertaleWadFile.Load(path);
            FilePath = Path.GetFileName(path);
            FileInfoText = string.Format(CultureInfo.InvariantCulture,
                "FORM {0:N0} bytes · {1} chunks · {2:N0} strings",
                _wad.FormLength, _wad.ChunkHeaders.Count, _wad.Strings?.RecordOffsets?.Count ?? 0);

            foreach (WadChunkHeader header in _wad.ChunkHeaders)
            {
                _wad.Chunks.TryGetValue(header.Name, out WadChunk chunk);
                Chunks.Add(WadChunkViewModel.Create(_wad, header, chunk));
            }
        }

        /// <summary>Supports the file menu path: MainWindow opens the file and hands it to this VM.</summary>
        public void Attach(UndertaleWadFile wad)
        {
            if (wad is null)
                throw new ArgumentNullException(nameof(wad));
            _wad?.Dispose();
            Chunks.Clear();
            ChunkEntries.Clear();

            _wad = wad;
            FilePath = string.IsNullOrEmpty(wad.FilePath) ? "(WAD)" : Path.GetFileName(wad.FilePath);
            FileInfoText = string.Format(CultureInfo.InvariantCulture,
                "FORM {0:N0} bytes · {1} chunks · {2:N0} strings",
                _wad.FormLength, _wad.ChunkHeaders.Count, _wad.Strings?.RecordOffsets?.Count ?? 0);

            foreach (WadChunkHeader header in _wad.ChunkHeaders)
            {
                _wad.Chunks.TryGetValue(header.Name, out WadChunk chunk);
                Chunks.Add(WadChunkViewModel.Create(_wad, header, chunk));
            }
        }

        private void RefreshChunkEntries(WadChunkViewModel chunk)
        {
            ChunkEntries.Clear();
            if (chunk?.Entries is null)
                return;
            foreach (WadEntryViewModel entry in chunk.Entries)
                ChunkEntries.Add(entry);
        }

        private void OpenFileDialog()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open GameMaker WAD file",
                Filter = "WAD files (*.wad)|*.wad|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true)
                return;
            try
            {
                LoadFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    $"Could not open the WAD file:\n{ex.Message}", "WAD Editor",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _wad?.Dispose();
            _wad = null;
        }
    }
}