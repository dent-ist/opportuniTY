import { _IdGenerator } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { Icon, IconName } from '../icon/icon';

/** One item of a tree; items with `children` are expandable groups. */
export interface TreeNode {
  readonly id: string;
  readonly label: string;
  readonly icon?: IconName;
  /** Short secondary text after the label (e.g. "Shared"); also read by screen readers. */
  readonly detail?: string;
  readonly children?: readonly TreeNode[];
}

interface Visible {
  readonly node: TreeNode;
  readonly level: number;
  readonly parent: string | null;
}

/**
 * Single-select tree on the WAI-ARIA APG tree pattern, with one Tab stop (roving tabindex).
 *
 * Keyboard: Up/Down move; Right expands a closed group or moves to its first child; Left collapses an open group
 * or moves to the parent; Home/End jump; Enter or Space activates the item (`activate`); `*` expands the
 * siblings. Clicking an item activates it; clicking a group's arrow only toggles it.
 */
@Component({
  selector: 'opp-tree',
  imports: [Icon, NgTemplateOutlet],
  template: `<ul class="tree" role="tree" [attr.aria-label]="label()">
      @for (node of nodes(); track node.id) {
        <ng-container
          [ngTemplateOutlet]="item"
          [ngTemplateOutletContext]="{ $implicit: node, level: 1 }"
        />
      }
    </ul>
    <ng-template #item let-node let-level="level">
      <li
        role="treeitem"
        class="tree__item"
        [id]="domId(node.id)"
        [attr.aria-level]="level"
        [attr.aria-expanded]="node.children ? isExpanded(node.id) : null"
        [attr.aria-selected]="node.id === selected()"
        [attr.aria-labelledby]="
          domId(node.id) + '-label' + (node.detail ? ' ' + domId(node.id) + '-detail' : '')
        "
        [tabIndex]="node.id === active() ? 0 : -1"
        (keydown)="onKeydown($event, node)"
        (click)="onClick($event, node)"
        (focus)="onFocus($event, node)"
      >
        <span
          class="tree__row"
          data-focus-indicator
          [class.is-selected]="node.id === selected()"
          [style.--tree-level]="level - 1"
        >
          @if (node.children) {
            <span class="tree__toggle" aria-hidden="true" (click)="onToggle($event, node)">
              <opp-icon [name]="isExpanded(node.id) ? 'chevron-down' : 'chevron-right'" />
            </span>
          } @else {
            <span class="tree__toggle" aria-hidden="true"></span>
          }
          @if (node.icon) {
            <opp-icon class="tree__icon" [name]="node.icon" />
          }
          <span class="tree__label" [id]="domId(node.id) + '-label'">{{ node.label }}</span>
          @if (node.detail) {
            <span class="tree__detail" [id]="domId(node.id) + '-detail'">{{ node.detail }}</span>
          }
        </span>
        @if (node.children && isExpanded(node.id)) {
          <ul role="group" class="tree__group">
            @for (child of node.children; track child.id) {
              <ng-container
                [ngTemplateOutlet]="item"
                [ngTemplateOutletContext]="{ $implicit: child, level: level + 1 }"
              />
            }
          </ul>
        }
      </li>
    </ng-template>`,
  styleUrl: './tree.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Tree {
  readonly nodes = input.required<readonly TreeNode[]>();
  readonly label = input.required<string>();
  /** Id of the selected item (`aria-selected`), or null. */
  readonly selected = input<string | null>(null);
  /** Groups open at first render; afterwards the user's toggles are kept. */
  readonly initiallyExpanded = input<readonly string[]>([]);
  readonly activate = output<TreeNode>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly prefix = inject(_IdGenerator).getId('opp-tree-');
  private readonly expanded = linkedSignal<ReadonlySet<string>>(
    () => new Set(this.initiallyExpanded()),
  );
  private readonly focused = signal<string | null>(null);

  private readonly visible = computed(() => {
    const out: Visible[] = [];
    const open = this.expanded();
    const walk = (nodes: readonly TreeNode[], level: number, parent: string | null) => {
      for (const node of nodes) {
        out.push({ node, level, parent });
        if (node.children && open.has(node.id)) walk(node.children, level + 1, node.id);
      }
    };
    walk(this.nodes(), 1, null);
    return out;
  });

  /** The item with the Tab stop: the focused one, else the selected one if shown, else the first. */
  protected readonly active = computed(() => {
    const ids = this.visible().map((v) => v.node.id);
    const focused = this.focused();
    if (focused && ids.includes(focused)) return focused;
    const selected = this.selected();
    if (selected && ids.includes(selected)) return selected;
    return ids[0] ?? null;
  });

  constructor() {
    // Reveal the selected item: open the groups above it.
    effect(() => {
      const selected = this.selected();
      if (!selected) return;
      const path = ancestors(this.nodes(), selected);
      if (path && path.some((id) => !this.expanded().has(id))) {
        this.expanded.update((open) => new Set([...open, ...path]));
      }
    });
  }

  protected domId(id: string): string {
    return `${this.prefix}-${id.replace(/[^\w-]/g, '_')}`;
  }

  isExpanded(id: string): boolean {
    return this.expanded().has(id);
  }

  toggle(id: string): void {
    this.expand(id, !this.isExpanded(id));
  }

  /** Moves focus into the tree (its Tab stop). */
  focus(): void {
    const id = this.active();
    if (id) this.focusItem(id);
  }

  expand(id: string, open = true): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (open) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  protected onFocus(event: FocusEvent, node: TreeNode): void {
    if (event.target === event.currentTarget) this.focused.set(node.id);
  }

  protected onClick(event: MouseEvent, node: TreeNode): void {
    event.stopPropagation();
    this.focused.set(node.id);
    this.activate.emit(node);
  }

  protected onToggle(event: MouseEvent, node: TreeNode): void {
    event.stopPropagation();
    this.focused.set(node.id);
    this.expand(node.id, !this.isExpanded(node.id));
    this.focusItem(node.id);
  }

  protected onKeydown(event: KeyboardEvent, node: TreeNode): void {
    if (event.target !== event.currentTarget || event.altKey || event.ctrlKey || event.metaKey)
      return;
    const list = this.visible();
    const index = list.findIndex((v) => v.node.id === node.id);
    const here = list[index];
    if (!here) return;
    let target: string | null = null;
    switch (event.key) {
      case 'ArrowDown':
        target = list[index + 1]?.node.id ?? null;
        break;
      case 'ArrowUp':
        target = list[index - 1]?.node.id ?? null;
        break;
      case 'Home':
        target = list[0]?.node.id ?? null;
        break;
      case 'End':
        target = list[list.length - 1]?.node.id ?? null;
        break;
      case 'ArrowRight':
        if (node.children) {
          if (!this.isExpanded(node.id)) this.expand(node.id);
          else target = node.children[0]?.id ?? null;
        }
        break;
      case 'ArrowLeft':
        if (node.children && this.isExpanded(node.id)) this.expand(node.id, false);
        else target = here.parent;
        break;
      case 'Enter':
      case ' ':
        this.activate.emit(node);
        break;
      case '*':
        for (const v of list)
          if (v.parent === here.parent && v.node.children) this.expand(v.node.id);
        break;
      default:
        return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (target) this.focusItem(target);
  }

  private focusItem(id: string): void {
    this.focused.set(id);
    // The item may only now be rendered (a group just opened): focus after this change detection.
    queueMicrotask(() =>
      this.host.nativeElement.querySelector<HTMLElement>(`#${CSS.escape(this.domId(id))}`)?.focus(),
    );
  }
}

/** Ids of the groups above `id` (outermost first), or null when it is not in the tree. */
function ancestors(nodes: readonly TreeNode[], id: string): string[] | null {
  for (const node of nodes) {
    if (node.id === id) return [];
    if (node.children) {
      const below = ancestors(node.children, id);
      if (below) return [node.id, ...below];
    }
  }
  return null;
}
