import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PdListComponent } from './pd-list/pd-list.component';
import { PdFormComponent } from './pd-form/pd-form.component';

const routes: Routes = [
    {
          path: "list",
          component: PdListComponent,
        },
        {
          path: "form",
          component: PdFormComponent,
        },
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PDRoutingModule { }
