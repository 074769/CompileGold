using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for ParameterAdder.xaml
    ///
    /// Shows every available option for a compile step as a checkbox. Checking/unchecking
    /// a row adds/removes that parameter from the active preset immediately - there's no
    /// separate "add" step. A dropdown at the top hides debugging/diagnostic options
    /// (leak checks, chart dumps, etc.) by default.
    /// </summary>
    public partial class ParameterAdder
    {
        /// <summary>
        /// Wraps a single available ConfigItem for display, and tracks whether it's
        /// currently enabled (present) in the active preset's parameter list.
        /// </summary>
        public class ParameterEntry : INotifyPropertyChanged
        {
            public ConfigItem Item { get; }
            private readonly ObservableCollection<ConfigItem> activeItems;

            public ParameterEntry(ConfigItem item, ObservableCollection<ConfigItem> activeItems)
            {
                Item = item;
                this.activeItems = activeItems;
            }

            public string Name => Item.Name;
            public string Parameter => Item.Parameter;
            public string Description => Item.Description;
            public string Warning => Item.Warning;
            public bool IsDebug => Item.IsDebug;

            // used by IsCompatiblePropertyGroup via reflection, mirrors ConfigItem.IsCompatible
            public bool IsCompatible => Item.IsCompatible;

            // Repeatable items (e.g. "Extra File", "Command Line Argument") can be added more
            // than once with different values, so a plain checked/unchecked toggle doesn't fit -
            // instead the getter always reports unchecked, and every check adds one more
            // instance (edited afterwards in the main parameter list). Non-repeatable items
            // behave as an ordinary present/absent toggle.
            public bool IsChecked
            {
                get => !Item.CanBeUsedMoreThanOnce && activeItems.Any(i => i.Name == Item.Name);
                set
                {
                    if (value)
                    {
                        if (Item.CanBeUsedMoreThanOnce || !activeItems.Any(i => i.Name == Item.Name))
                            activeItems.Add((ConfigItem)Item.Clone());
                    }
                    else
                    {
                        foreach (var existing in activeItems.Where(i => i.Name == Item.Name).ToList())
                            activeItems.Remove(existing);
                    }

                    OnPropertyChanged(nameof(IsChecked));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private readonly ObservableCollection<ParameterEntry> entries;

        public ParameterAdder(ObservableCollection<ConfigItem> configItems, ObservableCollection<ConfigItem> activeItems)
        {
            InitializeComponent();

            entries = new ObservableCollection<ParameterEntry>(
                configItems.Select(ci => new ParameterEntry(ci, activeItems)));

            ICollectionView paramView = CollectionViewSource.GetDefaultView(entries);
            using (paramView.DeferRefresh())
            {
                paramView.GroupDescriptions.Clear();
                paramView.GroupDescriptions.Add(new IsCompatiblePropertyGroup());
            }

            // default to hiding debugging options
            DebugFilterCombo.SelectedIndex = 0;
            paramView.Filter = o => !((ParameterEntry)o).IsDebug;

            ConfigDataGrid.ItemsSource = paramView;
        }

        private void DebugFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ICollectionView paramView = CollectionViewSource.GetDefaultView(entries);

            if (DebugFilterCombo.SelectedIndex == 0)
                paramView.Filter = o => !((ParameterEntry)o).IsDebug;
            else
                paramView.Filter = null;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
