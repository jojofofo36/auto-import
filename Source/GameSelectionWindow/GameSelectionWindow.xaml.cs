using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AutoImportPlugin
{
    public partial class GameSelectionWindow : Window
    {
        private List<ScannedGameWrapper> _games;

        public List<ScannedGameWrapper> SelectedGames
        {
            get
            {
                return _games
                    .Where(game => game.IsSelected)
                    .ToList();
            }
        }

        public bool EnableHdrSupport
        {
            get
            {
                return ChkEnableHdr.IsChecked == true;
            }
        }

        public string ControllerProfile
        {
            get
            {
                if (CmbController.SelectedItem is ComboBoxItem item)
                {
                    return item.Content?.ToString() ?? "PS4";
                }

                return "PS4";
            }
        }

        public GameSelectionWindow(List<ScannedGameWrapper> foundGames)
        {
            InitializeComponent();

            _games = foundGames;

            GridGames.ItemsSource = _games;
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
