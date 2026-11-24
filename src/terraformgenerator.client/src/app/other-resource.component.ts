import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { SketchStore } from './sketch.store';

// Any other resource, described in words: the AI picks the provider resource, fills its fields and proposes the box;
// the person approves or discards it on the drawing.
@Component({
  selector: 'app-other-resource',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.aiStatus()?.enabled) {
      <h2>Not sure of its name? Describe it</h2>
      <textarea rows="3" autocomplete="off" placeholder="Describe it, for example a Kubernetes cluster with 3 nodes, or a VPN to the office"
                [value]="text()" (input)="text.set(value($event))" [disabled]="store.previewing()"></textarea>
      <button type="button" [disabled]="text().trim().length < 3 || store.proposing() || store.previewing()" (click)="propose()">
        {{ store.proposing() ? 'Asking the AI…' : 'Propose the box' }}</button>
      <p class="hint">The AI writes the box with its fields; you review it on the drawing and approve or discard it.</p>
    }
  `,
  styles: `
    h2 { margin: 18px 0 6px; font-size: 15px; }
    textarea { width: 100%; padding: 6px 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface); color: var(--text); font: inherit; font-size: 13px; resize: vertical; }
    button { width: 100%; margin-top: 6px; padding: 6px 8px; border: 1px solid var(--accent); border-radius: 6px; background: var(--surface); color: var(--text); font: inherit; font-size: 13px; cursor: pointer; }
    button:disabled { opacity: 0.5; cursor: default; }
    .hint { margin: 6px 0; color: var(--muted); font-size: 12px; }
  `,
})
export class OtherResourceComponent {
  protected readonly store = inject(SketchStore);
  readonly proposed = output<void>();
  protected readonly text = signal('');

  protected value(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  protected async propose(): Promise<void> {
    const cloud = this.store.platform()?.label ?? 'cloud';
    const description = `Add one new box for: ${this.text().trim()}\n` +
      `Use a smart box if one fits; otherwise a box from the full ${cloud} provider list, with every field it requires and the ones that make it safe. ` +
      'Draw arrows to the boxes it uses, and keep every other box, id and arrow exactly as it is.';
    if (await this.store.propose(description, false)) {
      this.text.set('');
      this.proposed.emit();
    }
  }
}
