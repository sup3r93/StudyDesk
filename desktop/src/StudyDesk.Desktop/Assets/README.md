# StudyDesk application icon / Icona dell'applicazione StudyDesk

## English

`StudyDesk.svg` is the editable source of the original StudyDesk caption mark: the mint open-book
geometry already used in `MainWindow.xaml`. It was ported with AI assistance. No external artwork,
icon pack or font glyph was used. These assets use the repository's
[MIT license](../../../../LICENSE).

`StudyDesk.ico` contains seven transparent, 32-bit RGBA PNG frames: **16, 24, 32, 48, 64, 128 and
256 pixels**. Every frame is rendered directly from the vector rather than resized from another
bitmap.

To regenerate with the Windows WPF framework and no external library:

1. Reproduce the caption badge: 29 × 29 DIPs, corner radius 7, background `#263E3B`, and a Viewbox
   with margin 6.
2. Use the SVG's original path with transparent fill, `#93DEC9` stroke and stroke thickness 1.7. The
   SVG records its measured caption transform.
3. Render the badge at each size with `RenderTargetBitmap`, 96 DPI and `PixelFormats.Pbgra32`;
   encode each frame with `PngBitmapEncoder`.
4. Pack the PNG payloads into an ICO: type 1, seven directory entries, one color plane and 32 bits
   per pixel. Encode the 256-pixel dimension as zero in its directory entry.
5. Validate frame sizes, payload offsets, RGBA channels, transparent corners and decoding with
   `IconBitmapDecoder`. Inspect the 16-, 32- and 256-pixel results before replacing the assets.

## Italiano

`StudyDesk.svg` è il sorgente modificabile del simbolo originale di StudyDesk: la geometria del
libro color menta già presente nella barra di `MainWindow.xaml`. La conversione è stata realizzata
con assistenza AI. Non sono stati usati disegni esterni, raccolte di icone o caratteri grafici.
Questi asset seguono la [licenza MIT](../../../../LICENSE) della repository.

`StudyDesk.ico` contiene sette immagini PNG trasparenti RGBA a 32 bit: **16, 24, 32, 48, 64, 128 e
256 pixel**. Ogni dimensione viene disegnata direttamente dal vettore, senza ridimensionare un'altra
immagine raster.

Per rigenerare con WPF di Windows, senza librerie esterne:

1. Riprodurre il riquadro della barra: 29 × 29 DIP, raggio degli angoli 7, sfondo `#263E3B` e
   Viewbox con margine 6.
2. Usare il tracciato originale dello SVG con riempimento trasparente, contorno `#93DEC9` e spessore
   1.7. Lo SVG conserva la trasformazione misurata nella barra dell'applicazione.
3. Disegnare ogni dimensione con `RenderTargetBitmap`, 96 DPI e `PixelFormats.Pbgra32`; codificare
   ogni immagine con `PngBitmapEncoder`.
4. Inserire le immagini PNG in un ICO: tipo 1, sette voci di directory, un piano colore e 32 bit per
   pixel. La dimensione 256 va rappresentata con zero nella relativa voce.
5. Verificare dimensioni, posizioni dei dati, canali RGBA, angoli trasparenti e lettura con
   `IconBitmapDecoder`. Controllare visivamente le immagini da 16, 32 e 256 pixel prima di
   sostituire gli asset.
