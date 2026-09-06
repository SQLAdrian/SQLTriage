/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// What the browser measured about the animated backdrop, read back from bgConfig.js.
    /// Every field is an observation, not a setting: <see cref="Style"/> is what the user chose,
    /// the rest is what is actually on screen.
    /// </summary>
    public sealed class BackgroundStatus
    {
        /// <summary>The persisted choice: none | wave | particle.</summary>
        public string Style { get; set; } = "none";
        /// <summary>True when the canvas element for the chosen style is in the DOM.</summary>
        public bool CanvasPresent { get; set; }
        /// <summary>Marks/dots the canvas actually seeded. Zero means it drew nothing.</summary>
        public int Marks { get; set; }
        /// <summary>True when the browser reports prefers-reduced-motion: reduce.</summary>
        public bool ReducedMotion { get; set; }
        /// <summary>Selectors measured to be painting an opaque background OVER the canvas
        /// (the canvas sits at z-index -2, so any opaque ancestor box buries it). Empty when
        /// nothing was measured on top of it.</summary>
        public List<string> Occluders { get; set; } = new();
    }

    /// <summary>
    /// Turns a measured <see cref="BackgroundStatus"/> into the sentence Settings prints.
    ///
    /// <para>The rule this class exists to hold: <b>every sentence is conditioned on the same
    /// measurement it sits beside.</b> The old Appearance section printed one fixed blurb
    /// ("Both are idle-cheap — the animation stops once the field comes to rest") regardless of
    /// whether the chosen backdrop was drawing at all. A user who selected "wave" and saw a flat
    /// charcoal screen was told the feature was working. The status line below is derived from
    /// what the browser reported this instant, and there is no branch that prints an unmeasured
    /// claim: when nothing has been measured yet, it says exactly that.</para>
    /// </summary>
    public static class BackgroundStatusReporter
    {
        public const string NotMeasured = "Not measured yet — the backdrop reports its state once this page has finished loading.";

        public static string Describe(BackgroundStatus? status)
        {
            if (status == null) return NotMeasured;

            var style = (status.Style ?? "none").Trim().ToLowerInvariant();
            var label = style switch
            {
                "wave" => "The wave backdrop",
                "particle" => "The particle backdrop",
                _ => null
            };

            if (label == null)
                return "Flat backdrop selected — no animated layer is running.";

            var occluders = (status.Occluders ?? new List<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToList();

            if (!status.CanvasPresent)
                return $"{label} is selected, and no backdrop canvas is on the page. "
                     + "Nothing is being drawn — reload the app, and if it stays this way the backdrop failed to start.";

            if (status.Marks <= 0)
                return $"{label} is on the page and has drawn nothing (no marks were seeded). "
                     + "That is a defect, not a setting.";

            if (occluders.Count > 0)
                return $"{label} is on the page and drawing {status.Marks} marks, and it is not visible: "
                     + $"{string.Join(" and ", occluders)} paints an opaque background over it.";

            if (status.ReducedMotion)
                return $"{label} is on the page and drawing {status.Marks} marks as a single still frame. "
                     + "This browser reports \"reduce motion\", so the field is painted once and does not animate.";

            return $"{label} is on the page and drawing {status.Marks} marks. "
                 + "It animates on pointer movement and settles to a still frame at rest.";
        }
    }
}
