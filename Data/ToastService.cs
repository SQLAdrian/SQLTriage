/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;

namespace SQLTriage.Data
{
    public class ToastService
    {
        public event Action<ToastNotification>? OnShow;

        // Mute: when engaged, suppress ALL alert toasts until MutedUntil - criticals included,
        // because ShowAlert(critical: true) sets Type=Error with IsAlert=true and the test below
        // is on IsAlert. See pages-r1-07 and SuppressedAlertCount.
        private DateTime _mutedUntil = DateTime.MinValue;
        public bool IsMuted => DateTime.UtcNow < _mutedUntil;
        public DateTime MutedUntil => _mutedUntil;

        /// <summary>
        /// How many alert toasts the mute has dropped since it was last engaged.
        ///
        /// <para>pages-r1-07 (honesty hunt, 2026-08-28): the flood mute suppressed every alert
        /// toast for five minutes and NOTHING anywhere in the tree rendered that state - a
        /// repo-wide grep for IsMuted/MutedUntil returned only the definition here, the single
        /// consumer in ToastContainer, and a test-registry entry recording that the badge had been
        /// removed. The nav bell renders an unrelated flag. So the count exists to be shown: a
        /// suppression an operator cannot see is indistinguishable from silence.</para>
        /// </summary>
        public int SuppressedAlertCount { get; private set; }

        /// <summary>
        /// Raised whenever the mute is engaged or released, and on every alert the mute drops.
        ///
        /// <para>pages-r1-07 / lane9-09 (2026-08-28): without this the badge stated the OPPOSITE of
        /// what it had measured for as long as the flood lasted. A suppressed alert returns from
        /// <see cref="Show"/> before <see cref="OnShow"/> is raised, so the container never
        /// re-rendered while alerts were being dropped: <c>MuteFor</c> zeroes the count, the badge
        /// paints "No alerts have been withheld yet.", and it stays on that sentence - criticals
        /// included - for the whole five-minute window. A disclosure that freezes at zero while the
        /// thing it discloses is happening is the same defect as no disclosure at all.</para>
        ///
        /// <para>Separate from <see cref="OnShow"/> on purpose: OnShow carries a toast to paint, and
        /// a suppressed alert is precisely the case where there is no toast to carry.</para>
        /// </summary>
        public event Action? MuteStateChanged;

        /// <summary>Master notifications switch (nav, persisted via UserSettings).
        /// When false, suppress Success/Info/Warning/Alert toasts.
        ///
        /// <para>CORRECTION (pages-r1-07, 2026-08-28): this comment used to stop there, and both
        /// verdict passes flagged the divergence. The gate below tests
        /// <c>toast.Type != ToastType.Error</c>, and ShowAlert(critical: true) produces
        /// Type=Error - so a CRITICAL ALERT survives this switch too, not just a plain error.
        /// That is the intended behaviour; the doc simply did not say it.</para></summary>
        public bool Enabled { get; set; } = true;

        public void MuteFor(int minutes)
        {
            _mutedUntil = DateTime.UtcNow.AddMinutes(minutes);
            SuppressedAlertCount = 0;
            MuteStateChanged?.Invoke();
        }

        public void Unmute()
        {
            _mutedUntil = DateTime.MinValue;
            SuppressedAlertCount = 0;
            MuteStateChanged?.Invoke();
        }

        /// <summary>
        /// Announces that alert toasts are being withheld. Deliberately bypasses BOTH gates
        /// below: a notice that toasts are being suppressed cannot be delivered through the
        /// mechanism doing the suppressing, or it is unreachable in exactly the state it
        /// describes. It is not IsAlert, so it cannot re-trigger flood detection.
        /// </summary>
        public void ShowSuppressionNotice(string title, string message)
            => OnShow?.Invoke(new ToastNotification
            {
                Type = ToastType.Warning,
                Title = title,
                Message = message,
                Duration = 8000,
                IsAlert = false
            });

        public void ShowSuccess(string title, string message = "", int duration = 3000)
            => Show(new ToastNotification { Type = ToastType.Success, Title = title, Message = message, Duration = duration });

        public void ShowError(string title, string message = "", int duration = 5000)
            => Show(new ToastNotification { Type = ToastType.Error, Title = title, Message = message, Duration = duration, IsAlert = false });

        public void ShowWarning(string title, string message = "", int duration = 4000)
            => Show(new ToastNotification { Type = ToastType.Warning, Title = title, Message = message, Duration = duration });

        public void ShowInfo(string title, string message = "", int duration = 3000)
            => Show(new ToastNotification { Type = ToastType.Info, Title = title, Message = message, Duration = duration });

        /// Alert-sourced toast — smaller, subject to flood muting
        public void ShowAlert(string title, string message = "", bool critical = false, int duration = 6000)
            => Show(new ToastNotification { Type = critical ? ToastType.Error : ToastType.Warning, Title = title, Message = message, Duration = duration, IsAlert = true });

        private void Show(ToastNotification toast)
        {
            // Notifications switched off — suppress everything EXCEPT genuine
            // errors. Silent failure is worse than an unwanted toast.
            if (!Enabled && toast.Type != ToastType.Error) return;
            // Muted - suppress alert toasts (never suppress Error from non-alert sources).
            // pages-r1-07: this drops CRITICAL alerts too, since ShowAlert(critical: true) sets
            // Type=Error but also IsAlert=true, and the test is on IsAlert. Counting what is
            // dropped is what lets the container say so instead of the screen just going quiet.
            if (IsMuted && toast.IsAlert)
            {
                SuppressedAlertCount++;
                // Announced, not merely counted. Nothing else here reaches the container on this
                // path - OnShow below is never raised for a dropped alert - so this is the only
                // signal that lets the badge say the number it has just measured.
                MuteStateChanged?.Invoke();
                return;
            }
            OnShow?.Invoke(toast);
        }
    }

    public class ToastNotification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public ToastType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Duration { get; set; } = 3000;
        public bool IsVisible { get; set; }
        /// True when this toast was fired by the alert evaluation engine
        public bool IsAlert { get; set; }
    }

    public enum ToastType
    {
        Success,
        Error,
        Warning,
        Info
    }
}
