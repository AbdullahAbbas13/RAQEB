import { Component, Input } from '@angular/core';
import { SwaggerClient, FileParameter } from '../../../shared/services/Swagger/SwaggerClient.service';
import Swal from 'sweetalert2';


@Component({
  selector: 'app-upload',
  templateUrl: './upload.component.html',
  styleUrl: './upload.component.scss'
})
export class UploadComponent {
  @Input() HeaderTitle!: string;
  @Input() BtnTitle!: string;
  @Input() Type!: string;

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
      Swal.fire({
        icon: 'warning',
        title: 'تنبيه',
        text: 'الرجاء اختيار ملف أولاً',
        confirmButtonText: 'حسناً',
        confirmButtonColor: '#ffc107'
      });
      return;
    }

    this.uploading = true;
    try {
      const fileParam: FileParameter = {
        data: this.selectedFile,
        fileName: this.selectedFile.name
      };

      // Handle different upload types
      if (this.Type === 'PD') {
        this.swaggerClient.apiPDImportPost(fileParam).subscribe(
          (response) => {
            if (response.success) {
              Swal.fire({
                icon: 'success',
                title: 'تم بنجاح',
                text: 'تم رفع ملف PD بنجاح',
                confirmButtonText: 'حسناً',
                confirmButtonColor: '#28a745'
              });
            } else {
              Swal.fire({
                icon: 'error',
                title: 'خطأ',
                text: response.message || 'حدث خطأ أثناء رفع الملف',
                confirmButtonText: 'حسناً',
                confirmButtonColor: '#dc3545'
              });
            }
            this.resetFileInput();
          },
          (error) => {
            this.handleUploadError(error);
          }
        ).add(() => {
          this.uploading = false;
        });
      } else if (this.Type === 'LGD') {
        this.swaggerClient.apiLGDUploadPost(fileParam).subscribe(
          (response) => {
            Swal.fire({
              icon: 'success',
              title: 'تم بنجاح',
              text: 'تم رفع ملف LGD بنجاح',
              confirmButtonText: 'حسناً',
              confirmButtonColor: '#28a745'
            });
            this.resetFileInput();
          },
          (error) => {
            this.handleUploadError(error);
          }
        ).add(() => {
          this.uploading = false;
        });
      }
    } catch (error) {
      this.handleUploadError(error);
    }
  }

  private resetFileInput() {
    this.selectedFile = null;
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    if (fileInput) fileInput.value = '';
  }

  private handleUploadError(error: any) {
    console.error('Upload failed', error);
    Swal.fire({
      icon: 'error',
      title: 'خطأ',
      text: 'حدث خطأ أثناء رفع الملف',
      confirmButtonText: 'حسناً',
      confirmButtonColor: '#dc3545'
    });
    this.uploading = false;
  }
}