import { describe, it, expect, vi, beforeEach, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

// Image normalization decodes via <img>/canvas, which jsdom can't do — pass the
// blob through unchanged here; the real re-encode is exercised in the browser.
vi.mock('../lib/image', () => ({ normalizeImage: async (b: Blob) => b }));

import AiEntryPage from './AiEntryPage';

const _realFetch = globalThis.fetch;
const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

function renderPage(route = '/entry/ai?meal=Lunch&date=2026-06-14') {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <Routes>
        <Route path="/entry/ai" element={<AiEntryPage />} />
        <Route path="/" element={<div>Home</div>} />
        <Route path="/entry/new" element={<div>Manual</div>} />
      </Routes>
    </MemoryRouter>
  );
}

const aiOn = () => ({ ok: true, json: async () => ({ enabled: true, supportsText: true, supportsImages: true }) });
// The page fetches /api/ai/status and /api/barcode/status together at mount;
// barcode is call #2, so each test queues this right after the AI-status mock.
const bcOff = () => ({ ok: true, json: async () => ({ enabled: false }) });

function estimateOk(items: unknown[], overall = 0.7) {
  return { ok: true, json: async () => ({ ok: true, error: null, overallConfidence: overall, source: 'AiText', items }) };
}

const row = (over: Record<string, unknown> = {}) => ({
  name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330,
  protein: 62, carbs: 0, fat: 7, confidence: 0.9,
  matchedFoodId: 'food-1', matchedDefaultUoM: 'g', isNew: false, ...over,
});

describe('AiEntryPage', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => cleanup());
  afterAll(() => { globalThis.fetch = _realFetch; });

  it('estimate populates multiple review rows', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce(estimateOk([
        row({ name: 'Chicken Breast' }),
        row({ name: 'Broccoli', matchedFoodId: null, isNew: true, confidence: 0.4 }),
      ]));

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    await userEvent.type(screen.getByRole('textbox', { name: /what did you eat/i }), 'chicken and broccoli');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() => expect(screen.getByDisplayValue('Chicken Breast')).toBeInTheDocument());
    expect(screen.getByDisplayValue('Broccoli')).toBeInTheDocument();
    expect(screen.getByText('new')).toBeInTheDocument(); // unmatched row badged
  });

  it('deletes a row', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce(estimateOk([row({ name: 'Rice' }), row({ name: 'Beans' })]));

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    await userEvent.type(screen.getByRole('textbox', { name: /what did you eat/i }), 'rice and beans');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByDisplayValue('Rice');
    await userEvent.click(screen.getByRole('button', { name: 'Remove Beans' }));

    expect(screen.queryByDisplayValue('Beans')).toBeNull();
    expect(screen.getByDisplayValue('Rice')).toBeInTheDocument();
  });

  it('edits override AI values and are sent on save', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce(estimateOk([row({ name: 'Rice', quantity: 150, calories: 205 })]))
      .mockResolvedValueOnce({ ok: true, json: async () => [] }); // batch save

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    await userEvent.type(screen.getByRole('textbox', { name: /what did you eat/i }), 'rice');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    const calInput = await screen.findByDisplayValue('205');
    await userEvent.clear(calInput);
    await userEvent.type(calInput, '255');

    await userEvent.click(screen.getByRole('button', { name: /Save 1 item/ }));

    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(4));
    const [, opts] = mockFetch.mock.calls[3];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.items[0].calories).toBe(255);
    expect(body.items[0].source).toBe('AiText');
    expect(body.items[0].foodId).toBe('food-1');
  });

  it('refine re-requests with the accumulated note', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce(estimateOk([row({ name: 'Toast' })]))
      .mockResolvedValueOnce(estimateOk([row({ name: 'Wholemeal Toast' })]));

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    await userEvent.type(screen.getByRole('textbox', { name: /what did you eat/i }), 'toast');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByDisplayValue('Toast');
    await userEvent.type(screen.getByLabelText(/Add a clarification/), "it's wholemeal");
    await userEvent.click(screen.getByRole('button', { name: 'Refine' }));

    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(4));
    const [, opts] = mockFetch.mock.calls[3];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.notes).toEqual(["it's wholemeal"]);
  });

  it('photo tab: chosen file is sent to estimate/image and saved as AiPhoto', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn()) // supportsImages: true
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({ ok: true, error: null, overallConfidence: 0.6, source: 'AiPhoto', items: [row({ name: 'Pizza', matchedFoodId: null, isNew: true })] }),
      })
      .mockResolvedValueOnce({ ok: true, json: async () => [] }); // batch save

    renderPage();
    await screen.findByRole('tab', { name: 'Photo' });
    await userEvent.click(screen.getByRole('tab', { name: 'Photo' }));

    const file = new File(['fake-bytes'], 'meal.jpg', { type: 'image/jpeg' });
    await userEvent.upload(screen.getByLabelText('Choose a photo'), file);

    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByDisplayValue('Pizza');
    const [url, opts] = mockFetch.mock.calls[2];
    expect(url).toBe('/api/user/test-user-id/estimate/image');
    expect((opts as RequestInit).body).toBeInstanceOf(FormData);
    expect(((opts as RequestInit).body as FormData).get('image')).toBeInstanceOf(Blob);

    await userEvent.click(screen.getByRole('button', { name: /Save 1 item/ }));
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(4));
    const saveBody = JSON.parse((mockFetch.mock.calls[3][1] as RequestInit).body as string);
    expect(saveBody.items[0].source).toBe('AiPhoto');
  });

  it('hides the photo tab when the provider cannot do images', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => ({ enabled: true, supportsText: true, supportsImages: false }) })
      .mockResolvedValueOnce(bcOff());

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    expect(screen.queryByRole('tab', { name: 'Photo' })).toBeNull();
  });

  it('shows the manual fallback when estimation is unavailable', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(bcOff())
      .mockResolvedValueOnce({ ok: true, json: async () => ({ ok: false, error: "Couldn't estimate — enter it manually.", overallConfidence: 0, source: 'AiText', items: [] }) });

    renderPage();
    await screen.findByRole('textbox', { name: /what did you eat/i });
    await userEvent.type(screen.getByRole('textbox', { name: /what did you eat/i }), 'something');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/enter it manually/i));
  });

  it('shows a disabled notice when AI is off', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => ({ enabled: false, supportsText: false, supportsImages: false }) })
      .mockResolvedValueOnce(bcOff());

    renderPage();
    await waitFor(() => expect(screen.getByText(/isn't configured/i)).toBeInTheDocument());
    expect(screen.queryByRole('textbox', { name: /what did you eat/i })).toBeNull();
  });
});
