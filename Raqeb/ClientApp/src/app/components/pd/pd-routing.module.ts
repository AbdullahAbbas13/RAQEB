import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PdListComponent } from './pd-list/pd-list.component';
import { PdFormComponent } from './pd-form/pd-form.component';
import { TransitionMatrixComponent } from './transition-matrix/transition-matrix.component';
import { YearlyAvgTransitionMatrixComponent } from './yearly-avg-transition-matrix/yearly-avg-transition-matrix.component';
import { LongRunMatrixComponent } from './long-run-matrix/long-run-matrix.component';
import { ODRComponent } from './odr/odr.component';

const routes: Routes = [
    {
          path: "list",
          component: PdListComponent,
        },
        {
          path: "form",
          component: PdFormComponent,
        },
        {
          path: "display-transition-matrix",
          component: TransitionMatrixComponent,
        },
        {
          path: "yearly-avg-transition-matrix",
          component: YearlyAvgTransitionMatrixComponent,
        },
        {
          path: "long-run-matrix",
          component: LongRunMatrixComponent,
        },
        
        {
          path: "odr",
          component: ODRComponent,
        }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PDRoutingModule { }
