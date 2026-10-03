import { Directive } from '@angular/core';
import { Tab, TabContent, TabList, TabPanel, Tabs } from '@angular/aria/tabs';

// Product-styled tabs on the @angular/aria headless pattern (WAI-ARIA APG tabs). Styles live in
// src/styles/_tabs.scss because these are directives.
//
// Keyboard: Left/Right (Up/Down when vertical) move between tabs, Home/End jump to the first/last tab,
// Enter/Space select in `explicit` mode (focus selects in the default `follow` mode), Tab moves into the
// active panel.
//
// <div oppTabs>
//   <ul oppTabList [(selectedTab)]="mode" aria-label="Viewer mode">
//     <li oppTab value="text">Extracted Text</li> …
//   </ul>
//   <div oppTabPanel value="text"><ng-template ngTabContent>…</ng-template></div>
// </div>

@Directive({ selector: '[oppTabs]', hostDirectives: [Tabs] })
export class OppTabs {}

@Directive({
  selector: '[oppTabList]',
  hostDirectives: [
    {
      directive: TabList,
      inputs: ['selectedTab', 'orientation', 'selectionMode', 'wrap', 'disabled'],
      outputs: ['selectedTabChange'],
    },
  ],
  host: { class: 'opp-tab-list' },
})
export class OppTabList {}

@Directive({
  selector: '[oppTab]',
  hostDirectives: [{ directive: Tab, inputs: ['value', 'disabled'] }],
  host: { class: 'opp-tab' },
})
export class OppTab {}

@Directive({
  selector: '[oppTabPanel]',
  hostDirectives: [{ directive: TabPanel, inputs: ['value'] }],
  host: { class: 'opp-tab-panel' },
})
export class OppTabPanel {}

export const TABS = [OppTabs, OppTabList, OppTab, OppTabPanel, TabContent] as const;
