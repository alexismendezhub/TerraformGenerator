import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { SketchStore } from './sketch.store';

// Existing Terraform (.tf files) becomes a proposed drawing to review and approve.
@Component({
  selector: 'app-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="backdrop" (click)="closed.emit()"></div>
    <section class="dialog" role="dialog" aria-modal="true" aria-labelledby="import-title">
      <h2 id="import-title">Import Terraform</h2>
      <p class="hint">Choose the .tf files of an existing environment (all the files of its folder). Terraform Generator proposes the drawing; your current drawing changes only if you approve it.</p>
      <p class="hint small">Code downloaded from Terraform Generator has a sketch.json in the same folder: choose it too and every box goes back where it was.</p>
      <label class="file">
        <input type="file" accept=".tf,.json" multiple autocomplete="off" (change)="choose($event)" />
      </label>
      @if (files().length > 0) {
        <p class="hint small">{{ files().length }} file(s): {{ names() }}</p>
        @if (!hasDrawing()) {
          <p class="hint small">No sketch.json chosen: the boxes are placed in columns by kind.</p>
        }
      }
      <fieldset class="mode">
        <label><input type="radio" name="reader" autocomplete="off" [checked]="!useAi()" (change)="useAi.set(false)" /> Read it with rules (on this computer)</label>
        <label><input type="radio" name="reader" autocomplete="off" [checked]="useAi()" [disabled]="!store.aiStatus()?.enabled" (change)="useAi.set(true)" /> Read it with AI (understands more, explains what it leaves out)</label>
      </fieldset>
      @if (useAi()) {
        <p class="hint small">The files are sent to {{ store.aiStatus()?.provider === 'Anthropic' ? 'Claude (Anthropic)' : store.aiStatus()?.provider }}. Remove passwords and keys from them first.</p>
      }
      @if (store.proposalError(); as error) {
        <p class="message error" role="alert">{{ error }}</p>
      }
      <div class="buttons">
        <button type="button" (click)="closed.emit()">Cancel</button>
        <button type="button" class="primary" [disabled]="store.proposing() || !hasTerraform()" (click)="read()">
          {{ store.proposing() ? 'Reading…' : 'Propose a drawing' }}
        </button>
      </div>
    </section>
  `,
  styleUrl: './dialog.css',
})
export class ImportComponent {
  protected readonly store = inject(SketchStore);
  readonly closed = output<void>();
  protected readonly files = signal<File[]>([]);
  protected readonly useAi = signal(false);

  protected names(): string {
    return this.files().map(file => file.name).join(', ');
  }

  protected hasTerraform(): boolean {
    return this.files().some(file => file.name.toLowerCase().endsWith('.tf'));
  }

  protected hasDrawing(): boolean {
    return this.files().some(file => file.name.toLowerCase().endsWith('.json'));
  }

  protected choose(event: Event): void {
    this.files.set(Array.from((event.target as HTMLInputElement).files ?? []));
  }

  protected async read(): Promise<void> {
    if (await this.store.importTerraform(this.files(), this.useAi())) {
      this.closed.emit();
    }
  }
}
