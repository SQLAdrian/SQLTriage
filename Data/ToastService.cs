/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;

namespace SQLTriage.Data
{
    public class ToastService
    {
        public event Action<ToastNotification>? OnShow;

        // Mute: if true, suppress non-critical toasts until MutedUntil
        private DateTime _mutedUntil = DateTime.MinValue;
        public bool IsMuted => DateTime.UtcNow < _mutedUntil;
        public DateTime MutedUntil => _mutedUntil;

        public void MuteFor(int minutes)
        {
            _mutedUntil = DateTime.UtcNow.AddMinutes(minutes);
        }

        public void Unmute() => _mutedUntil = DateTime.MinValue;

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
            // Muted — suppress alert toasts (never suppress Error from non-alert sources)
            if (IsMuted && toast.IsAlert) return;
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
