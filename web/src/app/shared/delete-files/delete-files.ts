import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { errorMessage } from '../../core/api';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';

/**
 * Confirmation for deleting library video files from disk. Lists the files, offers to unmonitor what they belonged to
 * (so the next search doesn't download them again) and runs `action` with that choice.
 */
@Component({
  selector: 'kr-delete-files',
  imports: [Modal, FormsModule],
  templateUrl: './delete-files.html',
  styleUrl: './delete-files.scss',
})
export class DeleteFilesDialog {
  private readonly toasts = inject(Toasts);

  readonly title = input('Delete file');
  /** Paths shown to the user, relative to the title's folder. */
  readonly paths = input.required<string[]>();
  /** Label of the unmonitor checkbox ("Unmonitor the movie", …). */
  readonly unmonitorLabel = input('Unmonitor');
  /** Deletes the files; resolves when done. */
  readonly action = input.required<(unmonitor: boolean) => Promise<unknown>>();

  readonly closed = output<void>();
  /** The files were deleted (the dialog has nothing more to show). */
  readonly deleted = output<void>();

  protected unmonitor = false;
  protected readonly busy = signal(false);

  async confirm() {
    this.busy.set(true);
    try {
      await this.action()(this.unmonitor);
      this.deleted.emit();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
