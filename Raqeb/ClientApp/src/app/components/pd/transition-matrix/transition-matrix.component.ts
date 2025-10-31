// ============================================
// pd-matrix-viewer.component.ts
// ============================================

import { Component, OnInit } from '@angular/core';
import { PDMatrixFilterDto, SwaggerClient } from '../../../shared/services/Swagger/SwaggerClient.service';

interface PDTransitionCell {
  fromGrade: number;
  toGrade: number;
  count: number;
}

interface PDRowStat {
  fromGrade: number;
  totalCount: number;
  pdPercent: number;
}

interface PDTransitionMatrixItem {
  year: number;
  month: number;
  cells: PDTransitionCell[];
  rowStats: PDRowStat[];
}

interface MatrixData {
  [fromGrade: number]: {
    [toGrade: number]: number;
  };
}

@Component({
  templateUrl: './transition-matrix.component.html',
  styleUrl: './transition-matrix.component.scss'
})
export class TransitionMatrixComponent {
  selectedYear: number = 2018;
  selectedMonth: number = 1;
  viewMode: 'matrix' | 'stats' = 'matrix';
  
  matrixData: MatrixData = {};
  rowStats: PDRowStat[] = [];
  grades: number[] = [1, 2, 3, 4];
  months: string[] = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  
  loading: boolean = false;
  error: string | null = null;
  maxCount: number = 0;
  
  currentItem: any = null;

  constructor(private swaggerClient: SwaggerClient) {}

     filter :any= {
      year: this.selectedYear,
      month: this.selectedMonth,
      page: 1,
      pageSize: 1 ,
      poolId : 7,
      minGrade : 1,
      maxGrade : 4 ,
      version :1
    };

  ngOnInit(): void {
    this.loadData();
  }

  loadData(): void {
    this.loading = true;
    this.error = null;


  

    this.swaggerClient.apiPDTransitionMatricesPost(this.filter).subscribe({
      next: (response) => {
        if (response.items && response.items.length > 0) {
          this.currentItem = response.items[0];
          this.processMatrixData();
        }
        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to load PD Transition Matrix data';
        console.error('API Error:', err);
        this.loading = false;
      }
    });
  }

  processMatrixData(): void {
    if (!this.currentItem) return;

    // Initialize matrix
    this.matrixData = {};
    this.grades.forEach(from => {
      this.matrixData[from] = {};
      this.grades.forEach(to => {
        this.matrixData[from][to] = 0;
      });
    });

    // Fill matrix with data
    this.currentItem.cells.forEach(cell => {
      this.matrixData[cell.fromGrade][cell.toGrade] = cell.count;
    });

    // Set row stats
    this.rowStats = this.currentItem.rowStats.filter((stat, index, self) => 
      index === self.findIndex(s => s.fromGrade === stat.fromGrade)
    );

    // Calculate max count for color scaling
    this.maxCount = Math.max(
      ...Object.values(this.matrixData).flatMap(row => Object.values(row))
    );
  }

  changeMonth(delta: number): void {
    const newMonth = this.selectedMonth + delta;
    
    if (newMonth < 1) {
      this.selectedMonth = 12;
      this.selectedYear--;
    } else if (newMonth > 12) {
      this.selectedMonth = 1;
      this.selectedYear++;
    } else {
      this.selectedMonth = newMonth;
    }
    
    this.loadData();
  }

  onYearChange(year: number): void {
    this.selectedYear = year;
    this.loadData();
  }

  getColorClass(count: number): string {
    if (count === 0) return 'intensity-0';
    
    const intensity = Math.min(100, (count / this.maxCount) * 100);
    
    if (intensity < 20) return 'intensity-20';
    if (intensity < 40) return 'intensity-40';
    if (intensity < 60) return 'intensity-60';
    if (intensity < 80) return 'intensity-80';
    return 'intensity-100';
  }

  getPDColorClass(percent: number): string {
    if (percent < 1) return 'pd-low';
    if (percent < 5) return 'pd-medium';
    if (percent < 20) return 'pd-high';
    return 'pd-critical';
  }

  getPercentage(count: number, total: number): string {
    return total > 0 ? ((count / total) * 100).toFixed(2) : '0.00';
  }

  getMigrations(fromGrade: number): Array<{grade: number, count: number, percentage: string}> {
    const stat = this.rowStats.find(s => s.fromGrade === fromGrade);
    if (!stat) return [];

    return this.grades
      .map(toGrade => ({
        grade: toGrade,
        count: this.matrixData[fromGrade][toGrade],
        percentage: this.getPercentage(this.matrixData[fromGrade][toGrade], stat.totalCount)
      }))
      .filter(m => m.count > 0);
  }

  exportData(): void {
    // Implement export functionality
    console.log('Export data', this.currentItem);
  }

  setViewMode(mode: 'matrix' | 'stats'): void {
    this.viewMode = mode;
  }
}