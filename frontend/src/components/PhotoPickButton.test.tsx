import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PhotoPickButton } from './PhotoPickButton';

describe('PhotoPickButton', () => {
  it('opens the two-action sheet when the visible button is tapped', async () => {
    render(<PhotoPickButton label="Add a photo" onFile={() => {}} />);
    expect(screen.queryByRole('menuitem', { name: /take photo/i })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Add a photo' }));

    expect(screen.getByRole('menuitem', { name: /take photo/i })).toBeInTheDocument();
    expect(screen.getByRole('menuitem', { name: /choose from files/i })).toBeInTheDocument();
  });

  it('the camera input has capture="environment" and fires onFile', async () => {
    const onFile = vi.fn();
    render(<PhotoPickButton label="Add a photo" onFile={onFile} />);
    await userEvent.click(screen.getByRole('button', { name: 'Add a photo' }));

    const cameraInput = screen.getByLabelText(/— camera/) as HTMLInputElement;
    expect(cameraInput.getAttribute('capture')).toBe('environment');

    const file = new File(['bytes'], 'photo.jpg', { type: 'image/jpeg' });
    await userEvent.upload(cameraInput, file);

    expect(onFile).toHaveBeenCalledWith(file);
  });

  it('the library input has no capture attribute and fires onFile', async () => {
    const onFile = vi.fn();
    render(<PhotoPickButton label="Add a photo" onFile={onFile} />);
    await userEvent.click(screen.getByRole('button', { name: 'Add a photo' }));

    const libraryInput = screen.getByLabelText(/— files/) as HTMLInputElement;
    expect(libraryInput.hasAttribute('capture')).toBe(false);

    const file = new File(['bytes'], 'photo.jpg', { type: 'image/jpeg' });
    await userEvent.upload(libraryInput, file);

    expect(onFile).toHaveBeenCalledWith(file);
  });

  it('is disabled when the disabled prop is set', () => {
    render(<PhotoPickButton label="Add a photo" onFile={() => {}} disabled />);
    expect(screen.getByRole('button', { name: 'Add a photo' })).toBeDisabled();
  });
});
