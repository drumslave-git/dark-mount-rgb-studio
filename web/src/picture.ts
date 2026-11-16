// Turns a picture file into what a display key takes: 120 × 120 pixels as a PNG, transparency kept.

export const DISPLAY_KEY_SIZE = 120;

/** The picture in `file` fitted into 120 × 120, whole and centred, as a PNG data: URL. What it leaves free stays transparent. */
export async function fitPicture(file: Blob): Promise<string> {
  const url = URL.createObjectURL(file);
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    const size = DISPLAY_KEY_SIZE;
    const w = img.naturalWidth || size, h = img.naturalHeight || size; // an SVG may have no size of its own
    const k = Math.min(size / w, size / h);
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = size;
    const ctx = canvas.getContext('2d')!;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, (size - w * k) / 2, (size - h * k) / 2, w * k, h * k);
    return canvas.toDataURL('image/png');
  } finally {
    URL.revokeObjectURL(url);
  }
}
