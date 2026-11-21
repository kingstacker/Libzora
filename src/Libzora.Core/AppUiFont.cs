namespace Libzora.Core;

// Main-window UI font is independent of the reader body font.
// Keep KingHwaOldSong at zero so settings created before UI font selection retain the bundled face.
public enum AppUiFont
{
    KingHwaOldSong = 0,
    SystemDefault = 1
}
