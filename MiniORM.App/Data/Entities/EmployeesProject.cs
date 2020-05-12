using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace MiniORM.App.Data.Entities
{
    public  class EmployeesProject
    {
        [Key]
        [ForeignKey(nameof(Employee))]
        public int EmploeeId { get; set; }
        [Key]
        [ForeignKey(nameof(Project))]
        public int ProjectId { get; set; }
        public Employee Employee { get; set; }
        public Project Project { get; set; }
    }
}
