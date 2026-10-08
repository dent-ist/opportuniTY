import type { NormalizedRect } from './redaction-api';
import {
  type FrameRect,
  type Rotation,
  fromFrame,
  frameFromPoints,
  minimumSize,
  moveFrame,
  normalizeRotation,
  resizeFrame,
  toFrame,
} from './redaction-geometry';

const ROTATIONS: readonly Rotation[] = [0, 90, 180, 270];

function close(actual: FrameRect, expected: FrameRect): void {
  expect(actual.left).toBeCloseTo(expected.left);
  expect(actual.top).toBeCloseTo(expected.top);
  expect(actual.width).toBeCloseTo(expected.width);
  expect(actual.height).toBeCloseTo(expected.height);
}

describe('redaction geometry (ADR-012 §2)', () => {
  it('maps stored rectangles onto the rotated page and back without moving them', () => {
    const rects: NormalizedRect[] = [
      { x: 0, y: 0, w: 1_000_000, h: 1_000_000 },
      { x: 123_457, y: 654_321, w: 200_001, h: 33_333 },
      { x: 999_000, y: 0, w: 1_000, h: 785 },
    ];
    for (const rotation of ROTATIONS) {
      for (const rect of rects) {
        expect(fromFrame(toFrame(rect, rotation), rotation)).toEqual(rect);
      }
    }
  });

  it('puts the top-left corner of the page where a clockwise turn takes it', () => {
    const corner: NormalizedRect = { x: 0, y: 0, w: 100_000, h: 200_000 };
    close(toFrame(corner, 0), { left: 0, top: 0, width: 0.1, height: 0.2 });
    close(toFrame(corner, 90), { left: 0.8, top: 0, width: 0.2, height: 0.1 });
    close(toFrame(corner, 180), { left: 0.9, top: 0.8, width: 0.1, height: 0.2 });
    close(toFrame(corner, 270), { left: 0, top: 0.9, width: 0.2, height: 0.1 });
  });

  it('rounds outward, clamps to the page and keeps the minimum size', () => {
    expect(fromFrame({ left: 0.1000004, top: 0.2, width: 0.3, height: 0.1 }, 0)).toEqual({
      x: 100_000,
      y: 200_000,
      w: 300_001,
      h: 100_000,
    });
    expect(fromFrame({ left: -0.2, top: 0.95, width: 0.5, height: 0.2 }, 0)).toEqual({
      x: 0,
      y: 950_000,
      w: 300_000,
      h: 50_000,
    });
    expect(fromFrame({ left: 0.9999, top: 0.5, width: 0, height: 0 }, 0, 785)).toEqual({
      x: 999_215,
      y: 500_000,
      w: 785,
      h: 785,
    });
  });

  it('moves and resizes on the displayed page without leaving it or shrinking below the minimum', () => {
    const box = { left: 0.8, top: 0.1, width: 0.15, height: 0.1 };
    close(moveFrame(box, 0.1, -0.5), { ...box, left: 0.85, top: 0 });
    close(resizeFrame(box, { right: true, bottom: true }, 0.2, -0.2, 0.01), {
      left: 0.8,
      top: 0.1,
      width: 0.2,
      height: 0.01,
    });
    close(resizeFrame(box, { left: true, top: true }, -0.05, -0.05, 0.01), {
      left: 0.75,
      top: 0.05,
      width: 0.2,
      height: 0.15,
    });
    close(frameFromPoints(0.6, 0.7, 0.2, 1.4), { left: 0.2, top: 0.7, width: 0.4, height: 0.3 });
  });

  it('keeps a keyboard-moved box the same size on a turned page', () => {
    const rect: NormalizedRect = { x: 100_000, y: 100_000, w: 250_000, h: 40_000 };
    for (const rotation of ROTATIONS) {
      const moved = fromFrame(moveFrame(toFrame(rect, rotation), 0.01, 0.01), rotation);
      expect(moved.w).toBe(rect.w);
      expect(moved.h).toBe(rect.h);
    }
  });

  it('knows the 2 × 2 pixels at 300 DPI minimum and normalizes rotations', () => {
    expect(minimumSize(612, 792)).toBe(785);
    expect(normalizeRotation(-90)).toBe(270);
    expect(normalizeRotation(450)).toBe(90);
  });
});
