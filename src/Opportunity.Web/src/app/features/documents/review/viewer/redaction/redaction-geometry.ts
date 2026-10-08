import { NORMALIZED_SCALE, type NormalizedRect } from './redaction-api';

// ADR-012 §2: redactions are stored in normalized page space at rotation 0 (integers, millionths of the page). The
// viewer shows the page rotated; these helpers map between that space and fractions of the displayed (rotated) page
// frame, so drawing, moving and resizing happen in what the reviewer sees and rotation never moves a redaction.

/** A rectangle as fractions (0–1) of the displayed page frame, after rotation. */
export interface FrameRect {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

export type Rotation = 0 | 90 | 180 | 270;

/** Floating-point slack (in normalized units) so an edge that did not move never drifts by rounding. */
const EPSILON = 1e-3;

export function normalizeRotation(degrees: number): Rotation {
  return ((((Math.round(degrees / 90) * 90) % 360) + 360) % 360) as Rotation;
}

/** Page point (u, v as fractions at rotation 0) → frame point for a page turned `rotation` degrees clockwise. */
function pageToFrame(u: number, v: number, rotation: Rotation): [number, number] {
  switch (rotation) {
    case 90:
      return [1 - v, u];
    case 180:
      return [1 - u, 1 - v];
    case 270:
      return [v, 1 - u];
    default:
      return [u, v];
  }
}

function frameToPage(a: number, b: number, rotation: Rotation): [number, number] {
  switch (rotation) {
    case 90:
      return [b, 1 - a];
    case 180:
      return [1 - a, 1 - b];
    case 270:
      return [1 - b, a];
    default:
      return [a, b];
  }
}

/** Where a stored rectangle appears on the displayed frame. */
export function toFrame(rect: NormalizedRect, rotation: Rotation): FrameRect {
  const [x1, y1] = pageToFrame(rect.x / NORMALIZED_SCALE, rect.y / NORMALIZED_SCALE, rotation);
  const [x2, y2] = pageToFrame(
    (rect.x + rect.w) / NORMALIZED_SCALE,
    (rect.y + rect.h) / NORMALIZED_SCALE,
    rotation,
  );
  return {
    left: Math.min(x1, x2),
    top: Math.min(y1, y2),
    width: Math.abs(x2 - x1),
    height: Math.abs(y2 - y1),
  };
}

/**
 * The stored rectangle for a rectangle on the displayed frame: clamped to the page, rounded outward to whole
 * normalized units (a box is never smaller than drawn) and at least `min` units in each page direction.
 */
export function fromFrame(frame: FrameRect, rotation: Rotation, min = 1): NormalizedRect {
  const [u1, v1] = frameToPage(frame.left, frame.top, rotation);
  const [u2, v2] = frameToPage(frame.left + frame.width, frame.top + frame.height, rotation);
  const clamp = (n: number) => Math.min(NORMALIZED_SCALE, Math.max(0, n));
  let x = clamp(Math.floor(Math.min(u1, u2) * NORMALIZED_SCALE + EPSILON));
  let y = clamp(Math.floor(Math.min(v1, v2) * NORMALIZED_SCALE + EPSILON));
  let right = clamp(Math.ceil(Math.max(u1, u2) * NORMALIZED_SCALE - EPSILON));
  let bottom = clamp(Math.ceil(Math.max(v1, v2) * NORMALIZED_SCALE - EPSILON));
  if (right - x < min) {
    right = Math.min(NORMALIZED_SCALE, x + min);
    x = right - min;
  }
  if (bottom - y < min) {
    bottom = Math.min(NORMALIZED_SCALE, y + min);
    y = bottom - min;
  }
  return { x, y, w: right - x, h: bottom - y };
}

/** Moves a frame rectangle by (dx, dy) frame fractions, keeping it on the page. */
export function moveFrame(rect: FrameRect, dx: number, dy: number): FrameRect {
  return {
    ...rect,
    left: Math.min(1 - rect.width, Math.max(0, rect.left + dx)),
    top: Math.min(1 - rect.height, Math.max(0, rect.top + dy)),
  };
}

/** Which edges a resize moves: a corner handle moves two, a keyboard resize the right and bottom edges. */
export interface ResizeEdges {
  readonly left?: boolean;
  readonly top?: boolean;
  readonly right?: boolean;
  readonly bottom?: boolean;
}

/** Resizes a frame rectangle by moving `edges` by (dx, dy), never below `minSize` and never off the page. */
export function resizeFrame(
  rect: FrameRect,
  edges: ResizeEdges,
  dx: number,
  dy: number,
  minSize: number,
): FrameRect {
  let left = rect.left;
  let top = rect.top;
  let right = rect.left + rect.width;
  let bottom = rect.top + rect.height;
  if (edges.left) left = Math.min(right - minSize, Math.max(0, left + dx));
  if (edges.right) right = Math.max(left + minSize, Math.min(1, right + dx));
  if (edges.top) top = Math.min(bottom - minSize, Math.max(0, top + dy));
  if (edges.bottom) bottom = Math.max(top + minSize, Math.min(1, bottom + dy));
  return { left, top, width: right - left, height: bottom - top };
}

/** A rectangle from two frame points (a drag), clamped to the frame. */
export function frameFromPoints(ax: number, ay: number, bx: number, by: number): FrameRect {
  const clamp = (n: number) => Math.min(1, Math.max(0, n));
  const [x1, x2] = [clamp(Math.min(ax, bx)), clamp(Math.max(ax, bx))];
  const [y1, y2] = [clamp(Math.min(ay, by)), clamp(Math.max(ay, by))];
  return { left: x1, top: y1, width: x2 - x1, height: y2 - y1 };
}

/**
 * The smallest redaction in normalized units on a page of `widthPt` × `heightPt` (ADR-012 §2.5: 2 × 2 device pixels
 * at the 300 DPI production resolution); the larger of both directions, so it holds after rotation too.
 */
export function minimumSize(widthPt: number, heightPt: number): number {
  const side = (pt: number) => (pt > 0 ? Math.ceil((2 * NORMALIZED_SCALE) / ((pt / 72) * 300)) : 1);
  return Math.max(side(widthPt), side(heightPt));
}

export function sameRect(a: NormalizedRect, b: NormalizedRect): boolean {
  return a.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h;
}

/** A centred box for a new redaction made from the keyboard: 40 % of the page wide, 6 % high. */
export function defaultFrameBox(): FrameRect {
  return { left: 0.3, top: 0.47, width: 0.4, height: 0.06 };
}
