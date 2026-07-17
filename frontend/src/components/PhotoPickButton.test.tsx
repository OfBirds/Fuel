import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PhotoPickButton } from './PhotoPickButton';

describe('PhotoPickButton', () => {
  it('renders the two direct actions', () => {
    render(<PhotoPickButton label="Photo" onFile={() => {}} />);
    expect(screen.getByRole('button', { name: 'Take photo' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'File upload' })).toBeInTheDocument();
  });

  it('the camera input has capture="environment" and fires onFile', async () => {
    const onFile = vi.fn();
    render(<PhotoPickButton label="Photo" onFile={onFile} />);
    await userEvent.click(screen.getByRole('button', { name: 'Take photo' }));

    const cameraInput = screen.getByLabelText(/— camera/) as HTMLInputElement;
    expect(cameraInput.getAttribute('capture')).toBe('environment');

    const file = new File(['bytes'], 'photo.jpg', { type: 'image/jpeg' });
    await userEvent.upload(cameraInput, file);

    expect(onFile).toHaveBeenCalledWith(file);
  });

  it('the library input has no capture attribute and fires onFile', async () => {
    const onFile = vi.fn();
    render(<PhotoPickButton label="Photo" onFile={onFile} />);
    await userEvent.click(screen.getByRole('button', { name: 'File upload' }));

    const libraryInput = screen.getByLabelText(/— files/) as HTMLInputElement;
    expect(libraryInput.hasAttribute('capture')).toBe(false);

    const file = new File(['bytes'], 'photo.jpg', { type: 'image/jpeg' });
    await userEvent.upload(libraryInput, file);

    expect(onFile).toHaveBeenCalledWith(file);
  });

  it('both actions are disabled when the disabled prop is set', () => {
    render(<PhotoPickButton label="Photo" onFile={() => {}} disabled />);
    expect(screen.getByRole('button', { name: 'Take photo' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'File upload' })).toBeDisabled();
  });
});
