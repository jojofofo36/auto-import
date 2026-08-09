using System.ComponentModel;
using System.Runtime.CompilerServices;
using Playnite.SDK.Models;

namespace AutoImportPlugin
{
    public class ScannedGameWrapper : INotifyPropertyChanged
    {
        private bool isSelected;

        public bool IsSelected
        {
            get => isSelected;

            set
            {
                if (isSelected == value)
                    return;

                isSelected = value;

                OnPropertyChanged();

                if (isSelected)
                {
                    IsIgnored = false;
                }
            }
        }

        private bool isIgnored;

        public bool IsIgnored
        {
            get => isIgnored;

            set
            {
                if (isIgnored == value)
                    return;

                isIgnored = value;

                OnPropertyChanged();

                if (isIgnored)
                {
                    IsSelected = false;
                }
            }
        }

        public GameMetadata GameData
        {
            get;
            set;
        }

        public string Name
        {
            get
            {
                return GameData?.Name;
            }
        }

        public string ExecutablePath
        {
            get
            {
                if (GameData?.GameActions != null)
                {
                    foreach (var action in GameData.GameActions)
                    {
                        if (action != null &&
                            !string.IsNullOrWhiteSpace(
                                action.Path))
                        {
                            return action.Path;
                        }
                    }
                }

                return string.Empty;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(
            [CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(name)
            );
        }
    }
}
