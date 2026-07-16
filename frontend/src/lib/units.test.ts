import { describe, it, expect } from 'vitest';
import { refQty, refLabel, inferPreferredSystem, convertToSystem } from './units';

describe('refQty / refLabel', () => {
  it('returns the world-standard reference quantity for known units', () => {
    expect(refQty('g')).toBe(100);
    expect(refQty('ml')).toBe(100);
    expect(refQty('mg')).toBe(1000);
    expect(refQty('kg')).toBe(1);
    expect(refQty('l')).toBe(1);
    expect(refQty('oz')).toBe(1);
    expect(refQty('lb')).toBe(1);
    expect(refQty('cup')).toBe(1);
    expect(refQty('fl oz')).toBe(8);
    expect(refQty('piece')).toBe(1);
  });

  it('defaults to 1 for unknown/legacy units', () => {
    expect(refQty('smidgen')).toBe(1);
    expect(refLabel('smidgen')).toBe('per 1 smidgen');
  });

  it('returns the matching display label', () => {
    expect(refLabel('g')).toBe('per 100 g');
    expect(refLabel('fl oz')).toBe('per 8 fl oz');
  });
});

describe('inferPreferredSystem', () => {
  it('defaults to metric with no data', () => {
    expect(inferPreferredSystem([])).toBe('metric');
  });

  it('picks metric when metric usage dominates', () => {
    expect(inferPreferredSystem([
      { defaultUoM: 'g', usageCount: 10 },
      { defaultUoM: 'oz', usageCount: 1 },
    ])).toBe('metric');
  });

  it('picks imperial when imperial usage dominates', () => {
    expect(inferPreferredSystem([
      { defaultUoM: 'oz', usageCount: 10 },
      { defaultUoM: 'g', usageCount: 1 },
    ])).toBe('imperial');
  });

  it('ties default to metric', () => {
    expect(inferPreferredSystem([
      { defaultUoM: 'g', usageCount: 5 },
      { defaultUoM: 'oz', usageCount: 5 },
    ])).toBe('metric');
  });

  it('unused foods (null usageCount) still count once', () => {
    expect(inferPreferredSystem([
      { defaultUoM: 'oz', usageCount: null },
      { defaultUoM: 'lb', usageCount: null },
      { defaultUoM: 'g', usageCount: null },
    ])).toBe('imperial');
  });

  it('never counts neutral units', () => {
    expect(inferPreferredSystem([
      { defaultUoM: 'piece', usageCount: 100 },
      { defaultUoM: 'tbsp', usageCount: 100 },
    ])).toBe('metric'); // no votes at all -> default
  });
});

describe('convertToSystem', () => {
  it('converts oz -> g for a metric target', () => {
    expect(convertToSystem({ quantity: 1, uom: 'oz' }, 'metric')).toEqual({ quantity: 28.4, uom: 'g' });
  });

  it('converts g -> oz for an imperial target', () => {
    const r = convertToSystem({ quantity: 100, uom: 'g' }, 'imperial');
    expect(r.uom).toBe('oz');
    expect(r.quantity).toBeCloseTo(3.5, 1);
  });

  it('converts lb -> kg and back', () => {
    const kg = convertToSystem({ quantity: 1, uom: 'lb' }, 'metric');
    expect(kg).toEqual({ quantity: 0.5, uom: 'kg' });
    const lb = convertToSystem({ quantity: 1, uom: 'kg' }, 'imperial');
    expect(lb.uom).toBe('lb');
    expect(lb.quantity).toBeCloseTo(2.2, 1);
  });

  it('converts fl oz -> ml and back', () => {
    const ml = convertToSystem({ quantity: 1, uom: 'fl oz' }, 'metric');
    expect(ml).toEqual({ quantity: 29.6, uom: 'ml' });
    const flOz = convertToSystem({ quantity: 100, uom: 'ml' }, 'imperial');
    expect(flOz.uom).toBe('fl oz');
    expect(flOz.quantity).toBeCloseTo(3.4, 1);
  });

  it('converts cup -> ml (metric target) and l -> fl oz (imperial target)', () => {
    expect(convertToSystem({ quantity: 1, uom: 'cup' }, 'metric')).toEqual({ quantity: 240, uom: 'ml' });
    const flOz = convertToSystem({ quantity: 1, uom: 'l' }, 'imperial');
    expect(flOz).toEqual({ quantity: 33.8, uom: 'fl oz' });
  });

  it('never converts neutral units (tbsp/tsp/piece/slice/serving)', () => {
    for (const uom of ['tbsp', 'tsp', 'piece', 'slice', 'serving']) {
      expect(convertToSystem({ quantity: 2, uom }, 'metric')).toEqual({ quantity: 2, uom });
      expect(convertToSystem({ quantity: 2, uom }, 'imperial')).toEqual({ quantity: 2, uom });
    }
  });

  it('leaves an already-metric unit untouched for a metric target (and vice versa)', () => {
    expect(convertToSystem({ quantity: 5, uom: 'kg' }, 'metric')).toEqual({ quantity: 5, uom: 'kg' });
    expect(convertToSystem({ quantity: 5, uom: 'oz' }, 'imperial')).toEqual({ quantity: 5, uom: 'oz' });
  });

  it('carries other fields through unchanged', () => {
    const row = { quantity: 1, uom: 'oz', calories: 100, name: 'Cheese' };
    const r = convertToSystem(row, 'metric');
    expect(r.calories).toBe(100);
    expect(r.name).toBe('Cheese');
  });
});
