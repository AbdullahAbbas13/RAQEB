import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';

import { PDRoutingModule } from './pd-routing.module';
import { SharedModule } from '../../shared/shared.module';
import { PdFormComponent } from './pd-form/pd-form.component';
import { PdListComponent } from './pd-list/pd-list.component';


@NgModule({
  declarations: [PdFormComponent,PdListComponent],
  imports: [
    CommonModule,
    PDRoutingModule,
    SharedModule
  ]
})
export class PDModule { }
