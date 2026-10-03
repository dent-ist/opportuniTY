import { Routes } from '@angular/router';
import { devRoutes } from './dev/dev-routes';
import { Home } from './home';

export const routes: Routes = [{ path: '', component: Home, title: 'opportuniTY' }, ...devRoutes];
