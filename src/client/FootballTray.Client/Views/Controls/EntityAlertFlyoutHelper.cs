using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Core.Dto;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.Views.Controls;

public static class EntityAlertFlyoutHelper
{
    public static void ShowEntityAlertFlyout(
        FrameworkElement anchorElement,
        string entityId,
        string entityName,
        bool isTeam,
        IPreferencesService? prefsService = null,
        Action? onSaved = null) =>
        ShowAlertProfileFlyout(anchorElement, entityId, entityName, isTeam, prefsService, onSaved);

    public static void ShowAlertProfileFlyout(
        FrameworkElement anchorElement,
        string entityId,
        string entityName,
        bool isTeam,
        IPreferencesService? prefsService = null,
        Action? onSaved = null)
    {
        prefsService ??= App.CurrentPreferencesService ?? new PreferencesService();
        if (anchorElement == null || string.IsNullOrWhiteSpace(entityId) || prefsService == null) return;

        var prefs = prefsService.Current;
        EntityAlertProfileDto? currentProfile = null;
        string canonicalTargetId = CanonicalEntityIds.TryTranslateLegacyId(entityId, out var canon) ? canon : entityId;

        if (isTeam && prefs.TeamAlertProfiles != null)
        {
            if (prefs.TeamAlertProfiles.TryGetValue(entityId, out var tp) ||
                prefs.TeamAlertProfiles.TryGetValue(canonicalTargetId, out tp))
            {
                currentProfile = tp;
            }
        }
        else if (!isTeam && prefs.CompetitionAlertProfiles != null)
        {
            if (prefs.CompetitionAlertProfiles.TryGetValue(entityId, out var cp) ||
                prefs.CompetitionAlertProfiles.TryGetValue(canonicalTargetId, out cp))
            {
                currentProfile = cp;
            }
        }

        currentProfile ??= new EntityAlertProfileDto { Mode = "InheritGlobal" };

        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 10, MinWidth = 280, Padding = new Thickness(6) };

        var header = new TextBlock
        {
            Text = $"🔔 Alerts: {entityName}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        panel.Children.Add(header);

        var rbInherit = new RadioButton { Content = "Use Global Settings", GroupName = $"AlertMode_{entityId}" };
        var rbMuted = new RadioButton { Content = "Mute All Alerts", GroupName = $"AlertMode_{entityId}" };
        var rbCustom = new RadioButton { Content = "Custom Alerts", GroupName = $"AlertMode_{entityId}" };

        if (string.Equals(currentProfile.Mode, "Muted", StringComparison.OrdinalIgnoreCase)) rbMuted.IsChecked = true;
        else if (string.Equals(currentProfile.Mode, "Custom", StringComparison.OrdinalIgnoreCase)) rbCustom.IsChecked = true;
        else rbInherit.IsChecked = true;

        panel.Children.Add(rbInherit);
        panel.Children.Add(rbMuted);
        panel.Children.Add(rbCustom);

        var customPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(16, 2, 0, 4),
            Visibility = (rbCustom.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed
        };

        var cbRemind = new CheckBox { Content = "⏰ Match Kickoff Reminders", IsChecked = currentProfile.EnableKickoffReminder };
        var cbGoals = new CheckBox { Content = "⚽ Goal Alerts", IsChecked = currentProfile.EnableGoals };
        var cbCards = new CheckBox { Content = "🟥 Red Cards & Ejections", IsChecked = currentProfile.EnableRedCards };
        var cbStart = new CheckBox { Content = "⏱️ Match Kickoff / Start", IsChecked = currentProfile.EnableKickoff };
        var cbHT = new CheckBox { Content = "⏸️ Half-Time (HT)", IsChecked = currentProfile.EnableHalfTime };
        var cbFT = new CheckBox { Content = "🏁 Full-Time (FT)", IsChecked = currentProfile.EnableFullTime };
        var cbPostponed = new CheckBox { Content = "⚠️ Match Postponed / Rescheduled", IsChecked = currentProfile.EnablePostponed };
        var cbCancelled = new CheckBox { Content = "🚫 Match Cancelled", IsChecked = currentProfile.EnableCancelled };

        customPanel.Children.Add(cbRemind);
        customPanel.Children.Add(cbGoals);
        customPanel.Children.Add(cbCards);
        customPanel.Children.Add(cbStart);
        customPanel.Children.Add(cbHT);
        customPanel.Children.Add(cbFT);
        customPanel.Children.Add(cbPostponed);
        customPanel.Children.Add(cbCancelled);

        var soundLabel = new TextBlock
        {
            Text = "Sound Override",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 4, 0, 2)
        };
        customPanel.Children.Add(soundLabel);

        var soundCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var itemInherit = new ComboBoxItem { Content = "Use Global Sound", Tag = "InheritGlobal" };
        var itemStadium = new ComboBoxItem { Content = "Stadium Cheer / Whistle", Tag = "Stadium" };
        var itemWindows = new ComboBoxItem { Content = "Windows Default Chime", Tag = "WindowsDefault" };
        var itemMute = new ComboBoxItem { Content = "Mute Sound", Tag = "Mute" };

        soundCombo.Items.Add(itemInherit);
        soundCombo.Items.Add(itemStadium);
        soundCombo.Items.Add(itemWindows);
        soundCombo.Items.Add(itemMute);

        string currentSound = currentProfile.SoundOverride ?? "InheritGlobal";
        if (string.Equals(currentSound, "Stadium", StringComparison.OrdinalIgnoreCase)) soundCombo.SelectedItem = itemStadium;
        else if (string.Equals(currentSound, "WindowsDefault", StringComparison.OrdinalIgnoreCase)) soundCombo.SelectedItem = itemWindows;
        else if (string.Equals(currentSound, "Mute", StringComparison.OrdinalIgnoreCase)) soundCombo.SelectedItem = itemMute;
        else soundCombo.SelectedItem = itemInherit;

        customPanel.Children.Add(soundCombo);
        panel.Children.Add(customPanel);

        void UpdateVisibility()
        {
            customPanel.Visibility = (rbCustom.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        async void SaveProfile()
        {
            string mode = (rbMuted.IsChecked == true) ? "Muted" : (rbCustom.IsChecked == true) ? "Custom" : "InheritGlobal";
            string selectedSound = (soundCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "InheritGlobal";

            var updated = new EntityAlertProfileDto
            {
                Mode = mode,
                EnableKickoffReminder = cbRemind.IsChecked ?? true,
                EnableGoals = cbGoals.IsChecked ?? true,
                EnableRedCards = cbCards.IsChecked ?? true,
                EnableKickoff = cbStart.IsChecked ?? true,
                EnableHalfTime = cbHT.IsChecked ?? true,
                EnableFullTime = cbFT.IsChecked ?? true,
                EnablePostponed = cbPostponed.IsChecked ?? true,
                EnableCancelled = cbCancelled.IsChecked ?? true,
                SoundOverride = selectedSound
            };

            if (isTeam)
            {
                await prefsService.SetTeamAlertProfileAsync(entityId, updated);
            }
            else
            {
                await prefsService.SetCompetitionAlertProfileAsync(entityId, updated);
            }
            onSaved?.Invoke();
        }

        rbInherit.Checked += (s, e) => { UpdateVisibility(); SaveProfile(); };
        rbMuted.Checked += (s, e) => { UpdateVisibility(); SaveProfile(); };
        rbCustom.Checked += (s, e) => { UpdateVisibility(); SaveProfile(); };

        cbRemind.Checked += (s, e) => SaveProfile();
        cbRemind.Unchecked += (s, e) => SaveProfile();
        cbGoals.Checked += (s, e) => SaveProfile();
        cbGoals.Unchecked += (s, e) => SaveProfile();
        cbCards.Checked += (s, e) => SaveProfile();
        cbCards.Unchecked += (s, e) => SaveProfile();
        cbStart.Checked += (s, e) => SaveProfile();
        cbStart.Unchecked += (s, e) => SaveProfile();
        cbHT.Checked += (s, e) => SaveProfile();
        cbHT.Unchecked += (s, e) => SaveProfile();
        cbFT.Checked += (s, e) => SaveProfile();
        cbFT.Unchecked += (s, e) => SaveProfile();
        cbPostponed.Checked += (s, e) => SaveProfile();
        cbPostponed.Unchecked += (s, e) => SaveProfile();
        cbCancelled.Checked += (s, e) => SaveProfile();
        cbCancelled.Unchecked += (s, e) => SaveProfile();

        soundCombo.SelectionChanged += (s, e) => SaveProfile();

        flyout.Content = panel;
        flyout.ShowAt(anchorElement);
    }
}
