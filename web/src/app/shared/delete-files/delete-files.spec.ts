import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { button, settle, setupTest, text } from '../../../testing/test-setup';
import { DeleteFilesDialog } from './delete-files';

describe('DeleteFilesDialog', () => {
  async function render(action: (unmonitor: boolean) => Promise<unknown>) {
    setupTest();
    const fixture = TestBed.createComponent(DeleteFilesDialog);
    fixture.componentRef.setInput('paths', ['Season 01/a.mkv', 'Season 01/b.mkv']);
    fixture.componentRef.setInput('unmonitorLabel', 'Unmonitor the episodes');
    fixture.componentRef.setInput('action', action);
    await settle(fixture);
    return fixture;
  }

  it('lists the files and passes the unmonitor choice to the action', async () => {
    const action = vi.fn(() => Promise.resolve());
    const fixture = await render(action);
    const deleted = vi.fn();
    fixture.componentInstance.deleted.subscribe(deleted);
    const el: HTMLElement = fixture.nativeElement;

    expect([...el.querySelectorAll('[data-paths] li')].map(li => li.textContent)).toEqual(['Season 01/a.mkv', 'Season 01/b.mkv']);
    expect(text(el)).toContain('These 2 files are deleted');

    const checkbox = el.querySelector<HTMLInputElement>('[data-unmonitor]')!;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    await settle(fixture);
    button(el, 'Delete').click();
    await settle(fixture);

    expect(action).toHaveBeenCalledWith(true);
    expect(deleted).toHaveBeenCalled();
  });

  it('stays open when the deletion fails', async () => {
    const fixture = await render(() => Promise.reject(new Error('Permission denied')));
    const deleted = vi.fn();
    fixture.componentInstance.deleted.subscribe(deleted);

    button(fixture.nativeElement, 'Delete').click();
    await settle(fixture);

    expect(deleted).not.toHaveBeenCalled();
    expect(button(fixture.nativeElement, 'Delete').disabled).toBe(false);
  });
});
