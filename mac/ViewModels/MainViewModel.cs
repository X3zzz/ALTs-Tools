using AltsTools.Localization;
using AltsTools.Models;
using AltsTools.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AltsTools.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public ObservableCollection<ProfileDataBlock> TokenProfiles { get; }

        public TokenConverterViewModel Converter   { get; }
        public AltManagerViewModel     AltManager  { get; }
        public SkinChangerViewModel    SkinChanger { get; }
        public SettingsViewModel       Settings    { get; }

        private int _selectedNavIndex;
        public int SelectedNavIndex
        {
            get => _selectedNavIndex;
            set => SetField(ref _selectedNavIndex, value);
        }

        public MainViewModel()
        {
            TokenProfiles = new ObservableCollection<ProfileDataBlock>(
                ProfileService.Load() ?? new List<ProfileDataBlock>());

            Converter   = new TokenConverterViewModel();
            AltManager  = new AltManagerViewModel(TokenProfiles, Converter);
            SkinChanger = new SkinChangerViewModel(Converter);
            Settings    = new SettingsViewModel();

            Converter.OnProfileAdded += block =>
            {
                TokenProfiles.Add(block);
                var deduped = ProfileService.RemoveDuplicates(TokenProfiles.ToList());
                TokenProfiles.Clear();
                foreach (var b in deduped) TokenProfiles.Add(b);
                ProfileService.Save(TokenProfiles.ToList());
            };

            LocalizationManager.Instance.LanguageChanged += () =>
            {
                Converter.RefreshLocalizedText();
                AltManager.RefreshLocalizedText();
                SkinChanger.RefreshLocalizedText();
            };
        }
    }
}
