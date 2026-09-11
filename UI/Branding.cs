using System;
using System.Drawing;
using System.IO;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Provides the application branding (icon + logo image) from resources embedded in
    /// the IntraDeploy assembly. Nothing here touches the file system, so the EXE stays
    /// fully portable - no dependency on the original Images folder at runtime.
    /// </summary>
    public static class Branding
    {
        /// <summary>Manifest name of the embedded multi-size application icon.</summary>
        public const string IconResourceName = "IntraDeploy.ico";

        /// <summary>Manifest name of the embedded main branding image.</summary>
        public const string ImageResourceName = "IntraDeployBranding.png";

        private static Icon _icon;
        private static MemoryStream _imageStream; // must stay alive for the lifetime of _image (GDI+)
        private static Image _image;

        /// <summary>
        /// Application icon from the embedded ICO (16-256 px). Used for the title bar,
        /// taskbar and dialogs. The instance is shared and must not be disposed by callers.
        /// </summary>
        public static Icon AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    using (Stream stream = OpenResource(IconResourceName))
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        memory.Position = 0;
                        _icon = new Icon(memory); // Icon copies eagerly; no stream lifetime requirement
                    }
                }
                return _icon;
            }
        }

        /// <summary>Main branding image from the embedded PNG. Shared; do not dispose.</summary>
        public static Image BrandImage
        {
            get
            {
                if (_image == null)
                {
                    using (Stream stream = OpenResource(ImageResourceName))
                    {
                        _imageStream = new MemoryStream();
                        stream.CopyTo(_imageStream);
                        _imageStream.Position = 0;
                        _image = Image.FromStream(_imageStream);
                    }
                }
                return _image;
            }
        }

        /// <summary>Short version string for display, e.g. "1.0".</summary>
        public static string DisplayVersion
        {
            get
            {
                Version version = typeof(Branding).Assembly.GetName().Version;
                return version == null ? string.Empty : version.ToString(2);
            }
        }

        private static Stream OpenResource(string name)
        {
            var assembly = typeof(Branding).Assembly;
            Stream stream = assembly.GetManifestResourceStream(name);
            if (stream != null)
            {
                return stream;
            }

            // Resilience: fall back to a suffix match in case the resource was embedded
            // under the default namespace-qualified name.
            string suffix = "." + name;
            foreach (string candidate in assembly.GetManifestResourceNames())
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly.GetManifestResourceStream(candidate);
                }
            }

            throw new InvalidOperationException(
                "Embedded resource '" + name + "' was not found in " + assembly.GetName().Name + ".");
        }
    }
}
