// by porog
// Cross-platform rewrite: uses QiuForms (WPF / Avalonia / CLI) instead of WinForms and System.Drawing.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using QiuForms;
using ImageMagick;
using UndertaleModLib.Util;

EnsureDataLoaded();

List<UndertaleFont> fonts = Data.Fonts.Where(font => font?.Name?.Content is not null).ToList();
if (fonts.Count == 0)
{
    ScriptMessage("This data file has no fonts.");
    return;
}

int defaultFont = fonts.FindIndex(font => font.Name.Content == "fnt_maintext");
QiuForm picker = new("Select a font");
QiuChoice fontChoice = picker.Choice("Font", fonts.Select(font => font.Name.Content), defaultFont < 0 ? 0 : defaultFont);
picker.SubmitText = "OK";
if (ShowQiuForm(picker) != QiuDialogResult.Submit)
    return;

UndertaleFont font = fonts[fontChoice.SelectedIndex];

TextureWorker textureWorker = new();
List<Letter> letters = LoadLetters();
if (letters.Count == 0)
{
    ScriptMessage("This font has no glyphs.");
    return;
}

QiuForm form = new("Font editor - " + font.Name.Content);
form.SubmitText = "Close";
form.ShowCancelButton = false;

QiuPanel columns = form.Panel(QiuOrientation.Horizontal);

QiuPanel left = columns.Panel(QiuOrientation.Vertical);
QiuListBox list = left.List(height: 320, width: 260, id: "letters");
left.Button("Edit letter", _ => SelectLetter(list.SelectedIndex), id: "edit");
left.Button("Duplicate letter", _ => DuplicateLetter(list.SelectedIndex), id: "duplicate");
left.Button("Delete letter", _ => DeleteLetter(list.SelectedIndex), id: "delete");

QiuPanel right = columns.Panel(QiuOrientation.Vertical);
QiuCanvas canvas = right.Canvas(240, 240, id: "preview");
canvas.Zoom = 4;

QiuChoice drawColor = right.Choice("Draw color", ["Black", "Transparent", "White"], 0, id: "drawColor");
drawColor.UseRadioGroup = true;

QiuNumberBox widthBox = right.Number("Width", 1, minimum: 1, maximum: ushort.MaxValue, integer: true, id: "width");
QiuNumberBox heightBox = right.Number("Height", 1, minimum: 1, maximum: ushort.MaxValue, integer: true, id: "height");
QiuNumberBox shiftBox = right.Number("Shift", 0, minimum: short.MinValue, maximum: short.MaxValue, integer: true, id: "shift");
QiuNumberBox offsetBox = right.Number("Offset", 0, minimum: short.MinValue, maximum: short.MaxValue, integer: true, id: "offset");

right.Button("Apply size", _ => ApplySize(), id: "applySize");
right.Button("Save letter", _ => SaveLetter(), id: "saveLetter");

QiuPanel bottom = form.Panel(QiuOrientation.Vertical);
QiuLabel status = bottom.Label("", heading: false);
bottom.Button("Save font", _ => SaveFont(), id: "saveFont");
bottom.Button("Import font from ZIP", _ => ImportZip(), id: "importZip");
bottom.Button("Export font to ZIP", _ => ExportZip(), id: "exportZip");

Letter? editing = null;

canvas.PointerPressed += args => PaintPixel(args);
canvas.PointerMoved += args =>
{
    if (args.IsPressed)
        PaintPixel(args);
};

status.Text = $"Editing {letters.Count} letters. Changes are applied to the data file only with \"Save font\".";
RefreshList();
if (letters.Count > 0)
{
    list.SelectedIndex = 0;
    SelectLetter(0);
}

ShowQiuForm(form);

// ---------------------------------------------------------------------------------------------
// Letters
// ---------------------------------------------------------------------------------------------

List<Letter> LoadLetters()
{
    List<Letter> result = [];

    using IMagickImage<byte> sheet = textureWorker.GetTextureFor(font.Texture, null);
    PixelImage page = PixelImage.FromGm(GMImage.FromMagickImage(sheet));

    foreach (UndertaleFont.Glyph glyph in font.Glyphs)
    {
        PixelImage? bitmap = null;
        if (glyph.SourceWidth > 0 && glyph.SourceHeight > 0)
            bitmap = page.Crop(glyph.SourceX, glyph.SourceY, glyph.SourceWidth, glyph.SourceHeight);

        result.Add(new Letter(glyph.Character, bitmap, glyph.Shift, glyph.Offset));
    }

    result.Sort((a, b) => a.Character.CompareTo(b.Character));
    return result;
}

void RefreshList()
{
    int previous = list.SelectedIndex;
    list.SetItems(letters.Select(letter =>
        $"{(char)letter.Character}   ({(letter.Character)}){(letter.Bitmap is null ? "  [empty]" : "")}"));
    if (previous >= 0 && previous < letters.Count)
        list.SelectedIndex = previous;
}

void SelectLetter(int index)
{
    if (index < 0 || index >= letters.Count)
        return;

    editing = letters[index];
    PixelImage bitmap = editing.Bitmap ?? new PixelImage(1, 1);

    widthBox.SetValueFromHost(bitmap.Width);
    heightBox.SetValueFromHost(bitmap.Height);
    shiftBox.SetValueFromHost(editing.Shift);
    offsetBox.SetValueFromHost(editing.Offset);

    // The canvas keeps a fixed size; the zoom is chosen so the largest glyph fits.
    canvas.Zoom = Math.Max(1, Math.Min(8, 240 / Math.Max(1, Math.Max(bitmap.Width, bitmap.Height))));
    canvas.Paint = context => PaintLetter(context, editing);
    canvas.Invalidate();
}

void PaintLetter(IQiuCanvasContext context, Letter? letter)
{
    context.FillRectangle(QiuColor.FromRgb(32, 32, 32), 0, 0, canvas.Width, canvas.Height);

    if (letter?.Bitmap is null)
        return;

    PixelImage bitmap = letter.Bitmap;
    double zoom = canvas.Zoom;
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            (byte b, byte g, byte r, byte a) = bitmap.Get(x, y);
            if (a == 0)
                continue;

            // The editor shows glyphs inverted, like the original WinForms version.
            context.FillRectangle(new QiuColor(a, (byte)(255 - r), (byte)(255 - g), (byte)(255 - b)),
                                  x * zoom, y * zoom, zoom, zoom);
        }
    }
}

void PaintPixel(QiuPointerEventArgs args)
{
    if (editing?.Bitmap is null)
        return;

    int x = (int)(args.X / canvas.Zoom);
    int y = (int)(args.Y / canvas.Zoom);
    if (x < 0 || y < 0 || x >= editing.Bitmap.Width || y >= editing.Bitmap.Height)
        return;

    switch (Math.Clamp(drawColor.SelectedIndex, 0, 2))
    {
        case 0:
            editing.Bitmap.Set(x, y, 255, 255, 255, 255);
            break;
        case 1:
            editing.Bitmap.Set(x, y, 255, 255, 255, 127);
            break;
        case 2:
            editing.Bitmap.Set(x, y, 255, 255, 255, 0);
            break;
    }

    canvas.Invalidate();
}

void ApplySize()
{
    if (editing?.Bitmap is null)
        return;

    int width = Math.Max(1, (int)widthBox.Value);
    int height = Math.Max(1, (int)heightBox.Value);
    if (width == editing.Bitmap.Width && height == editing.Bitmap.Height)
        return;

    PixelImage resized = new(width, height);
    resized.Blit(editing.Bitmap, 0, 0);
    editing.Bitmap = resized;

    canvas.Zoom = Math.Max(1, Math.Min(8, 240 / Math.Max(1, Math.Max(width, height))));
    canvas.Invalidate();
    editing.Dirty = true;
}

void SaveLetter()
{
    if (editing is null)
        return;

    editing.Shift = (short)shiftBox.Value;
    editing.Offset = (short)offsetBox.Value;
    editing.Dirty = true;
    RefreshList();
    status.Text = $"Saved letter {(char)editing.Character} (not written to the data file yet).";
}

void DuplicateLetter(int index)
{
    if (index < 0 || index >= letters.Count)
        return;

    Letter source = letters[index];
    Letter copy = new((ushort)(source.Character + 1), source.Bitmap?.Clone(), source.Shift, source.Offset) { Dirty = true };
    letters.Add(copy);
    letters.Sort((a, b) => a.Character.CompareTo(b.Character));
    RefreshList();
    status.Text = $"Duplicated letter {(char)source.Character}.";
}

void DeleteLetter(int index)
{
    if (index < 0 || index >= letters.Count)
        return;

    Letter letter = letters[index];
    letters.RemoveAt(index);
    RefreshList();
    editing = null;
    canvas.Paint = null;
    canvas.Invalidate();
    status.Text = $"Deleted letter {(char)letter.Character} (not written to the data file yet).";
}

// ---------------------------------------------------------------------------------------------
// Font saving
// ---------------------------------------------------------------------------------------------

void SaveFont()
{
    if (letters.Count == 0)
    {
        status.Text = "There are no letters to save.";
        return;
    }

    List<Letter> ordered = [.. letters];
    ordered.Sort((a, b) => (b.Bitmap?.Height ?? 0).CompareTo(a.Bitmap?.Height ?? 0));

    PixelImage sheet = new(font.Texture.SourceWidth, font.Texture.SourceHeight);
    List<(int X, int Y)> positions = [];

    const int gap = 2;
    int xPos = gap;
    int yPos = gap;
    int rowMaxHeight = -1;

    foreach (Letter letter in ordered)
    {
        int width = letter.Bitmap?.Width ?? 0;
        int height = letter.Bitmap?.Height ?? 0;

        if (height > rowMaxHeight)
            rowMaxHeight = height;
        if (xPos + width > sheet.Width)
        {
            xPos = gap;
            yPos += rowMaxHeight + gap;
            rowMaxHeight = -1;
        }

        positions.Add((xPos, yPos));

        if (xPos + width > sheet.Width || yPos + height > sheet.Height)
        {
            status.Text = "All characters do not fit in the texture.";
            return;
        }

        if (letter.Bitmap is not null)
            sheet.Blit(letter.Bitmap, xPos, yPos);

        xPos += width + gap;
    }

    PixelImage page = PixelImage.FromGm(font.Texture.TexturePage.TextureData.Image);
    page.Fill(font.Texture.SourceX, font.Texture.SourceY, sheet.Width, sheet.Height, 255, 255, 255, 0);
    page.Blit(sheet, font.Texture.SourceX, font.Texture.SourceY);

    font.Texture.TexturePage.TextureData.Image = GMImage.FromPng(page.ToPng());
    font.Texture.TargetX = 0;
    font.Texture.TargetY = 0;

    List<UndertaleFont.Glyph> glyphs = [];
    for (int i = 0; i < ordered.Count; i++)
    {
        Letter letter = ordered[i];
        glyphs.Add(new UndertaleFont.Glyph
        {
            Character = letter.Character,
            SourceX = (ushort)positions[i].X,
            SourceY = (ushort)positions[i].Y,
            SourceWidth = (ushort)(letter.Bitmap?.Width ?? 0),
            SourceHeight = (ushort)(letter.Bitmap?.Height ?? 0),
            Shift = letter.Shift,
            Offset = letter.Offset,
        });
    }

    glyphs.Sort((a, b) => a.Character.CompareTo(b.Character));
    font.Glyphs.Clear();
    foreach (UndertaleFont.Glyph glyph in glyphs)
        font.Glyphs.Add(glyph);

    font.RangeStart = glyphs[0].Character;
    font.RangeEnd = glyphs[^1].Character;

    foreach (Letter letter in letters)
        letter.Dirty = false;

    status.Text = "Font saved to the data file.";
}

// ---------------------------------------------------------------------------------------------
// ZIP import/export
// ---------------------------------------------------------------------------------------------

const string ExtraCharDataFile = "otherletters.csv";

void ImportZip()
{
    string? path = PromptLoadFile("zip", "ZIP files (*.zip)|*.zip|All files (*.*)|*.*");
    if (path is null)
        return;

    List<Letter> imported = [];

    using (ZipArchive archive = ZipFile.OpenRead(path))
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = entry.FullName.Replace(".png", "").Split(';');
                ushort character = ushort.Parse(ValueOf(parts[0]));
                short shift = short.Parse(ValueOf(parts[1]));
                short offset = short.Parse(ValueOf(parts[2]));

                using Stream stream = entry.Open();
                using MemoryStream memory = new();
                stream.CopyTo(memory);
                imported.Add(new Letter(character, PixelImage.FromPng(memory.ToArray()), shift, offset) { Dirty = true });
            }
            else if (entry.FullName.Equals(ExtraCharDataFile, StringComparison.Ordinal))
            {
                using Stream stream = entry.Open();
                using StreamReader reader = new(stream, Encoding.ASCII);
                foreach (string line in reader.ReadToEnd().Split('\n'))
                {
                    if (line.Length == 0)
                        continue;

                    string[] parts = line.Split(';');
                    imported.Add(new Letter(ushort.Parse(ValueOf(parts[0])), null,
                                            short.Parse(ValueOf(parts[2])), short.Parse(ValueOf(parts[3])))
                    {
                        Dirty = true,
                    });
                }
            }
        }
    }

    letters = imported;
    RefreshList();
    status.Text = $"Imported {letters.Count} letters (not written to the data file yet).";

    static string ValueOf(string part) => part[(part.IndexOf('=') + 1)..];
}

void ExportZip()
{
    string? path = PromptSaveFile("zip", "ZIP files (*.zip)|*.zip|All files (*.*)|*.*");
    if (path is null)
        return;

    using FileStream stream = File.Create(path);
    using ZipArchive archive = new(stream, ZipArchiveMode.Create);

    StringBuilder extra = new();
    bool hasExtra = false;

    foreach (Letter letter in letters)
    {
        if (letter.Bitmap is not null)
        {
            string name = $"char={letter.Character};shift={letter.Shift};offset={letter.Offset}.png";
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream entryStream = entry.Open();
            byte[] png = letter.Bitmap.ToPng();
            entryStream.Write(png, 0, png.Length);
        }
        else
        {
            hasExtra = true;
            extra.Append($"char={letter.Character};width=0;height=0;shift={letter.Shift};offset={letter.Offset}\n");
        }
    }

    if (hasExtra)
    {
        if (extra.Length > 0)
            extra.Length--;

        ZipArchiveEntry entry = archive.CreateEntry(ExtraCharDataFile);
        using Stream entryStream = entry.Open();
        byte[] content = Encoding.ASCII.GetBytes(extra.ToString());
        entryStream.Write(content, 0, content.Length);
    }

    status.Text = "Exported successfully.";
}

// ---------------------------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------------------------

class Letter
{
    public ushort Character { get; }
    public PixelImage? Bitmap { get; set; }
    public short Shift { get; set; }
    public short Offset { get; set; }
    public bool Dirty { get; set; }

    public Letter(ushort character, PixelImage? bitmap, short shift, short offset)
    {
        Character = character;
        Bitmap = bitmap;
        Shift = shift;
        Offset = offset;
    }
}

/// <summary>A portable BGRA32 image buffer; replaces System.Drawing.Bitmap.</summary>
class PixelImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public PixelImage(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new byte[Width * Height * 4];
    }

    public static PixelImage FromPng(byte[] png) => FromGm(GMImage.FromPng(png));

    public static PixelImage FromGm(GMImage image)
    {
        GMImage raw = image.Format == GMImage.ImageFormat.RawBgra ? image : image.ConvertToRawBgra();
        PixelImage result = new(raw.Width, raw.Height);
        byte[] source = raw.GetRawImageDataArray();
        Array.Copy(source, result.Pixels, Math.Min(source.Length, result.Pixels.Length));
        return result;
    }

    public byte[] ToPng()
    {
        GMImage image = new(Width, Height);
        Array.Copy(Pixels, image.GetRawImageDataArray(), Pixels.Length);
        return image.ConvertToPng().GetData();
    }

    public (byte B, byte G, byte R, byte A) Get(int x, int y)
    {
        int index = (y * Width + x) * 4;
        return (Pixels[index], Pixels[index + 1], Pixels[index + 2], Pixels[index + 3]);
    }

    public void Set(int x, int y, byte b, byte g, byte r, byte a)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;

        int index = (y * Width + x) * 4;
        Pixels[index] = b;
        Pixels[index + 1] = g;
        Pixels[index + 2] = r;
        Pixels[index + 3] = a;
    }

    public PixelImage Clone()
    {
        PixelImage copy = new(Width, Height);
        Array.Copy(Pixels, copy.Pixels, Pixels.Length);
        return copy;
    }

    public PixelImage Crop(int x, int y, int width, int height)
    {
        PixelImage result = new(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int source = ((y + row) * Width + (x + column)) * 4;
                int target = (row * result.Width + column) * 4;
                Array.Copy(Pixels, source, result.Pixels, target, 4);
            }
        }
        return result;
    }

    public void Blit(PixelImage source, int x, int y)
    {
        for (int row = 0; row < source.Height; row++)
        {
            for (int column = 0; column < source.Width; column++)
            {
                int index = (row * source.Width + column) * 4;
                Set(x + column, y + row,
                    source.Pixels[index], source.Pixels[index + 1],
                    source.Pixels[index + 2], source.Pixels[index + 3]);
            }
        }
    }

    public void Fill(int x, int y, int width, int height, byte b, byte g, byte r, byte a)
    {
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
                Set(x + column, y + row, b, g, r, a);
        }
    }
}
