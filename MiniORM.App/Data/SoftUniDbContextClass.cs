
namespace MiniORM.App.Data
{
    using Entities;
    public class SoftUniDbContext : DbContext
    {



        public SoftUniDbContext(string connectionString) : base(connectionString)
    {
    }
    public DbSet<Employee> Employees { get; }
    public DbSet<Department> Departmens { get; }
    public DbSet<Project> Projects { get; }
    public DbSet<EmployeesProject> EmployeesProjects { get; set; }
}
}
