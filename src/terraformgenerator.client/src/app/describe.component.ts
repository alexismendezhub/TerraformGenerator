import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { SketchStore } from './sketch.store';

// The person describes what they need; the AI proposes a drawing that they review before it replaces anything.
@Component({
  selector: 'app-describe',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="backdrop" (click)="closed.emit()"></div>
    <section class="dialog" role="dialog" aria-modal="true" aria-labelledby="describe-title">
      <h2 id="describe-title">Describe your architecture</h2>
      <p class="hint">Say what you need in your own words. The AI proposes a drawing; nothing changes until you approve it.</p>
      <textarea rows="6" autocomplete="off" spellcheck="true" [value]="text()" (input)="text.set(value($event))"
                placeholder="For example: an online store with a web site and an API behind Front Door on shop.example.com, a private orders database, files for invoices and a Key Vault. Production."></textarea>
      @if (store.sketch().nodes.length > 0) {
        <fieldset class="mode">
          <label><input type="radio" name="mode" autocomplete="off" [checked]="!fresh()" (change)="fresh.set(false)" /> Change the current drawing</label>
          <label><input type="radio" name="mode" autocomplete="off" [checked]="fresh()" (change)="fresh.set(true)" /> Start a new drawing</label>
        </fieldset>
      }
      @if (store.aiStatus(); as status) {
        @if (status.problem) {
          <p class="message warning">{{ status.problem }}</p>
        }
        <p class="hint small">Your description and the current drawing are sent to {{ status.provider === 'Anthropic' ? 'Claude (Anthropic)' : status.provider }}{{ status.model ? ', ' + status.model : '' }}. Do not include passwords or keys.</p>
      }
      @if (store.proposalError(); as error) {
        <p class="message error" role="alert">{{ error }}</p>
      }
      <div class="buttons">
        <button type="button" (click)="closed.emit()">Cancel</button>
        <button type="button" class="primary" [disabled]="store.proposing() || text().trim().length === 0" (click)="propose()">
          {{ store.proposing() ? 'Thinking…' : 'Propose a drawing' }}
        </button>
      </div>
    </section>
  `,
  styleUrl: './dialog.css',
})
export class DescribeComponent {
  protected readonly store = inject(SketchStore);
  readonly closed = output<void>();
  protected readonly text = signal('');
  protected readonly fresh = signal(false);

  protected value(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  protected async propose(): Promise<void> {
    if (await this.store.propose(this.text().trim(), this.fresh())) {
      this.closed.emit();
    }
  }
}
