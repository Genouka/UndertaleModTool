// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>A static image.</summary>
/// <remarks>
/// Scripts normally pass encoded PNG bytes, because that is what the rest of the tool produces
/// (texture pages, sprite exports, room renders) and it keeps this assembly free of image codecs.
/// </remarks>
public sealed class QiuImage : QiuElement
{
    /// <summary>Creates an image from encoded PNG bytes.</summary>
    public QiuImage(byte[] png)
    {
        Png = png ?? throw new ArgumentNullException(nameof(png));
    }

    /// <summary>Creates an image from a file the host can read.</summary>
    public QiuImage(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>Encoded PNG bytes, or <see langword="null"/> when the image comes from a file.</summary>
    public byte[]? Png { get; }

    /// <summary>Path of the image file, or <see langword="null"/> when the image comes from bytes.</summary>
    public string? FilePath { get; }

    /// <summary>Optional caption shown under the image.</summary>
    public string? Caption { get; set; }

    /// <summary>Maximum height in device-independent units, or <c>0</c> for no limit.</summary>
    public double MaxHeight { get; set; }
}
