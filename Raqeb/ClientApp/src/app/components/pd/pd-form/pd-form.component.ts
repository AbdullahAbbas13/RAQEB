import { Component } from '@angular/core';
import { SwaggerClient, FileParameter, ApiResponseOfString } from '../../../shared/services/Swagger/SwaggerClient.service';
import Swal from 'sweetalert2';

@Component({
  selector: 'app-pd-form',
  templateUrl: './pd-form.component.html',
  styleUrl: './pd-form.component.scss'
})
export class PdFormComponent {
  selectedFile: File | null = null;
  uploading: boolean = false;

  constructor(private swaggerClient: SwaggerClient) {}

  onFileSelected(event: any) {
    const file = event.target.files[0];
    if (file) {
      this.selectedFile = file;
    }
  }

  async uploadFile() {
    if (!this.selectedFile) {
      alert('Please select a file first');
      return;
    }

    this.uploading = true;
    try {
      const fileParam: FileParameter = {
        data: this.selectedFile,
        fileName: this.selectedFile.name
      };

      this.swaggerClient.apiPDImportPost(fileParam).subscribe(
        (response: ApiResponseOfString) => {
          if (response.success) {
            Swal.fire({
              icon: 'success',
              title: '?? ?????',
              text: 'PD ?? ??? ????? ?????',
              confirmButtonText: '?????',
              confirmButtonColor: '#28a745'
            });
          } else {
            Swal.fire({
              icon: 'error',
              title: '???',
              text: response.message || '??? ??? ????? ??? ?????',
              confirmButtonText: '?????',
              confirmButtonColor: '#dc3545'
            });
          }
          this.selectedFile = null;
          // Reset the file input
          const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
          if (fileInput) fileInput.value = '';
        },
        (error) => {
          console.error('Upload failed', error);
          Swal.fire({
            icon: 'error',
            title: '???',
            text: '??? ??? ????? ??? ?????',
            confirmButtonText: '?????',
            confirmButtonColor: '#dc3545'
          });
        }
      ).add(() => {
        this.uploading = false;
      });
    } catch (error) {
      console.error('Error during upload', error);
      Swal.fire({
        icon: 'error',
        title: '???',
        text: '??? ??? ????? ??? ?????',
        confirmButtonText: '?????',
        confirmButtonColor: '#dc3545'
      });
      this.uploading = false;
    }
  }
}
