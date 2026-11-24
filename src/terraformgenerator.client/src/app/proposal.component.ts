import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ProposalChange } from './models';
import { SketchStore } from './sketch.store';

// The AI proposal under review: what needs a look, what changes, the checks, and approve, correct or discard.
@Component({
  selector: 'app-proposal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.proposal(); as proposal) {
      <h2>Proposal</h2>
      <p class="hint">Nothing has changed yet. Review the proposal on the canvas, then approve it or ask for changes.</p>
      @if (proposal.summary) {
        <p class="summary">{{ proposal.summary }}</p>
      }

      @if (proposal.review.length > 0) {
        <h3>Needs your review ({{ proposal.review.length }})</h3>
        <ul class="review">
          @for (item of proposal.review; track $index) {
            <li><span class="flag" aria-hidden="true">!</span>{{ item.text }}</li>
          }
        </ul>
      }

      @if (problems().length > 0) {
        <h3>Checks</h3>
        @for (message of problems(); track $index) {
          <p class="message" [class]="message.level"><span class="badge">{{ message.level === 'error' ? 'Error' : 'Warning' }}</span>{{ message.text }}</p>
        }
      }

      <h3>Changes ({{ proposal.changes.length }})</h3>
      @if (proposal.changes.length === 0) {
        <p class="hint">The proposal is the same as your drawing.</p>
      } @else {
        <ul class="changes">
          @for (change of proposal.changes; track $index) {
            <li [class]="change.type"><span class="sign" aria-hidden="true">{{ sign(change) }}</span><span class="visually-hidden">{{ change.type }}: </span>{{ change.text }}</li>
          }
        </ul>
      }

      <h3>Correct it</h3>
      <textarea rows="3" autocomplete="off" spellcheck="true" [value]="correction()" (input)="correction.set(value($event))"
                placeholder="For example: make the API Python and add a storage account for reports."></textarea>
      @if (store.proposalError(); as error) {
        <p class="message error" role="alert">{{ error }}</p>
      }
      <div class="actions">
        <button type="button" [disabled]="store.proposing() || correction().trim().length === 0" (click)="correct()">
          {{ store.proposing() ? 'Thinking…' : 'Ask for changes' }}
        </button>
      </div>

      <div class="decision">
        <button type="button" class="primary" [disabled]="store.proposing()" (click)="store.approveProposal()">Approve</button>
        <button type="button" [disabled]="store.proposing()" (click)="store.discardProposal()">Discard</button>
      </div>
      @if (errorCount() > 0) {
        <p class="hint small">Approving keeps the errors on the drawing for you to fix.</p>
      }
    }
  `,
  styleUrl: './panel.css',
})
export class ProposalComponent {
  protected readonly store = inject(SketchStore);
  protected readonly correction = signal('');
  protected readonly problems = computed(() => (this.store.proposal()?.messages ?? []).filter(message => message.level !== 'info'));
  protected readonly errorCount = computed(() => this.problems().filter(message => message.level === 'error').length);

  protected sign(change: ProposalChange): string {
    return change.type === 'added' ? '+' : change.type === 'removed' ? '−' : '~';
  }

  protected value(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  protected async correct(): Promise<void> {
    if (await this.store.propose(this.correction().trim(), false)) {
      this.correction.set('');
    }
  }
}
