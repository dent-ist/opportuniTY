import {
  CdkMenu,
  CdkMenuGroup,
  CdkMenuItem,
  CdkMenuItemRadio,
  CdkMenuTrigger,
} from '@angular/cdk/menu';

// Menus on the CDK menu pattern (WAI-ARIA APG menu button). Styles live in src/styles/_menu.scss because
// the panel renders in an overlay.
//
// Keyboard: Enter / Space / Down on the trigger opens the menu and focuses the first item; Up/Down move,
// Home/End jump, typing a letter jumps to the matching item, Enter / Space activate, Escape closes and
// returns focus to the trigger, Tab closes the menu.
//
// <button type="button" [cdkMenuTriggerFor]="menu">Admin</button>
// <ng-template #menu>
//   <div cdkMenu class="opp-menu">
//     <a cdkMenuItem class="opp-menu__item" routerLink="…">Fields</a>
//   </div>
// </ng-template>
export const MENU = [CdkMenuTrigger, CdkMenu, CdkMenuItem, CdkMenuGroup, CdkMenuItemRadio] as const;
