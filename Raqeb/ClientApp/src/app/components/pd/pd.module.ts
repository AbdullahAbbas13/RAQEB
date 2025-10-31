import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';

import { PDRoutingModule } from './pd-routing.module';
import { SharedModule } from '../../shared/shared.module';
import { PdFormComponent } from './pd-form/pd-form.component';
import { PdListComponent } from './pd-list/pd-list.component';
import { TransitionMatrixComponent } from './transition-matrix/transition-matrix.component';
import { YearlyAvgTransitionMatrixComponent } from './yearly-avg-transition-matrix/yearly-avg-transition-matrix.component';
import { LongRunMatrixComponent } from './long-run-matrix/long-run-matrix.component';


@NgModule({
  declarations: [
    PdFormComponent,
    PdListComponent,
    TransitionMatrixComponent,
    YearlyAvgTransitionMatrixComponent,
    LongRunMatrixComponent
  ],
  imports: [
    CommonModule,
    PDRoutingModule,
    SharedModule
  ]
})
export class PDModule { }
