import { describe, it, expect, beforeEach } from 'vitest';
import {
  getAutoUpdate, getFontScale, saveFontScale,
  getTheme, saveTheme,
} from '../lib/storage';

// localStorage is available in the jsdom environment by default.

describe('storage', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  describe('app: key prefix', () => {
    it('namespaces stored keys', () => {
      saveTheme('dark');
      const keys = Object.keys(localStorage);
      expect(keys).toContain('app:theme');
    });
  });

  describe('getAutoUpdate', () => {
    it('returns true by default', () => {
      expect(getAutoUpdate()).toBe(true);
    });
  });

  describe('getFontScale', () => {
    it('returns 100 by default', () => {
      expect(getFontScale()).toBe(100);
    });

    it('round-trips a saved value', () => {
      saveFontScale(140);
      expect(getFontScale()).toBe(140);
    });
  });

  describe('malformed JSON', () => {
    it('returns null when the stored value is not valid JSON', () => {
      localStorage.setItem('app:theme', '{not valid json}');
      expect(getTheme()).toBeNull();
    });
  });

  describe('theme', () => {
    it('saves and reads theme', () => {
      saveTheme('dark');
      expect(getTheme()).toBe('dark');

      saveTheme('light');
      expect(getTheme()).toBe('light');
    });
  });
});
