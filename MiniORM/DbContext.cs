using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;

namespace MiniORM
{
	public abstract class DbContext
	{
		private readonly DatabaseConnection connection;
		private readonly Dictionary<Type, PropertyInfo> dbSetProperties;
		internal static readonly Type[] AllowedSqlTypes =
		{
			typeof(string),
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(decimal),
			typeof(bool),
			typeof(DateTime)
		};

		protected DbContext(string connectionString)
		{
			this.connection = new DatabaseConnection(connectionString);
			this.dbSetProperties = this.DiscoverDbSets();
			using(new ConnectionManager(connection))
			{
				this.InitializeDbSets();
			}
			this.MapAllRelations();
		}
		public void SaveChanges()
		{
			var dbSets = this.dbSetProperties.Select(pi => pi.Value.GetValue(this)).ToArray();
			foreach (IEnumerable<Object> dbSet in dbSets)
			{
				var invalidEnteties = dbSet.Where(entity => !IsObjectValid(entity)).ToArray();
				if (invalidEnteties.Any())
				{
					throw new InvalidOperationException($"{invalidEnteties.Length} Invalit Entities found in {dbSet.GetType().Name}!");

				}
			}
			using (new ConnectionManager(connection))
			{
				using (var transaction = this.connection.StartTransaction())
				{
					foreach (var dbSet in dbSets)
					{
						var dbSetType = dbSet.GetType().GetGenericArguments().First();
						var persistMethod = typeof(DbContext).GetMethod("Persist", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(dbSetType);
						try
						{
							persistMethod.Invoke(this, new object[] { dbSet });
						}
						catch (TargetInvocationException tie)
						{

							throw tie.InnerException;
						}
						catch (InvalidOperationException)
						{
							transaction.Rollback();
							throw;
						}
						catch (SqlException)
						{
							transaction.Rollback();
							throw;
						}
					}
					transaction.Commit();
				}
			}
		}

		private void Persist<TEntity>(DbSet<TEntity> dbSet) where TEntity :class, new()
		{
			var tableName = GetTableName(typeof(TEntity));
			var columns = this.connection.FetchColumnNames(tableName).ToArray();
			if (dbSet.ChangeTracker.Added.Any())
			{
				this.connection.InsertEntities(dbSet.ChangeTracker.Added, tableName, columns);
			}
			var modifiedEntites = dbSet.ChangeTracker.GetModifiedEntities(dbSet).ToArray();
			if (modifiedEntites.Any())
			{
				this.connection.DeleteEntities(dbSet.ChangeTracker.Removed, tableName, columns);
			}
		}
		private void InitializeDbSets()
		{
			foreach (var dbSet in this.dbSetProperties)
			{
				var dbSetType = dbSet.Key;
				var dbSetProperty = dbSet.Value;
				var populateDbSetGeneric = typeof(DbContext).GetMethod("PopulateDbSet", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(dbSetType);
				populateDbSetGeneric.Invoke(this, new object[] { dbSetProperty });
			}
		}
		private void PopulateDbSet<TEntity>(PropertyInfo dbSet) where TEntity :class, new()
		{
			var entities = LoadTableEntities<TEntity>();
			var dbSetInstance = new DbSet<TEntity>(entities);
			ReflectionHelper.ReplaceBackingField(this, dbSet.Name, dbSetInstance);
		}

		private void MapAllRelations()
		{
			foreach (var dbSetProperty in this.dbSetProperties)
			{
				var dbSetType = dbSetProperty.Key;
				var mapRelationGeneric = typeof(DbContext).GetMethod("MapRelations", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(dbSetType);
				var dbSet = dbSetProperty.Value.GetValue(this);
				mapRelationGeneric.Invoke(this, new[] { dbSet });
			}
		}

		private void MapRelations<TEntity>(DbSet<TEntity> dbSet) where TEntity :class, new()
		{
			var entityType = typeof(TEntity);
			MapNavigationProperties(dbSet);
			var collections = entityType.GetProperties().Where(pi => pi.PropertyType.IsGenericType && pi.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>)).ToArray();
			foreach (var collection in collections)
			{
				var collectionType = collection.PropertyType.GenericTypeArguments.First();
				var mapCollectionMethod = typeof(DbContext).GetMethod("MapCollection", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(entityType, collectionType);
				mapCollectionMethod.Invoke(this, new object[] { dbSet, collection });
			}
		}

		private void MapCollection<TDBSet,TCollection>(DbSet<TDBSet> dbSet,PropertyInfo collectionProperty) where TDBSet :class,new() where TCollection :class, new()
		{
			var entityType = typeof(TDBSet);
			var collectionType = typeof(TCollection);
			var primaryKeys = collectionType.GetProperties().Where(pi => pi.HasAttribute<KeyAttribute>()).ToArray();
			var primaryKey = primaryKeys.First();
			var foreignKey = entityType.GetProperties().First(pi => pi.HasAttribute<KeyAttribute>());
			var isManyToMany = primaryKeys.Length >= 2;
			if (isManyToMany)
			{
				primaryKey = collectionType.GetProperties().First(pi => collectionType.GetProperty(pi.GetCustomAttribute<ForeignKeyAttribute>().Name).PropertyType == entityType);
			}
			var navigationDbSet = (DbSet<TCollection>)this.dbSetProperties[collectionType].GetValue(this);
			foreach (var entity in dbSet)
			{
				var primaryKeyValue = foreignKey.GetValue(entity);
				var navigationEntities = navigationDbSet.Where(navigationEntity => primaryKey.GetValue(navigationEntity).Equals(primaryKeyValue)).ToArray();
				ReflectionHelper.ReplaceBackingField(entity, collectionProperty.Name, navigationEntities);
			}
		}

		private void MapNavigationProperties<TEntity>(DbSet<TEntity> dbSet) where TEntity : class, new()
		{
			var entityType = typeof(TEntity);
			var foreignKeys = entityType.GetProperties().Where(pi=>pi.HasAttribute<ForeignKeyAttribute>()).ToArray();
			foreach (var foreignKey in foreignKeys)
			{
				var navigationPropertyName = foreignKey.GetCustomAttribute<ForeignKeyAttribute>().Name;
				var navigationPropery = entityType.GetProperty(navigationPropertyName);
				var navigationDbSet = this.dbSetProperties[navigationPropery.PropertyType].GetValue(this);
				var navigationPrimaryKey = navigationPropery.PropertyType.GetProperties().First(pi => pi.HasAttribute<KeyAttribute>());
				foreach (var entity in dbSet)
				{
					var foreignKeyValue = foreignKey.GetValue(entity);
					var navigationPropertyValue = ((IEnumerable<object>)navigationDbSet).First(currentNavigationProperty => navigationPrimaryKey.GetValue(currentNavigationProperty).Equals(foreignKeyValue));
					navigationPropery.SetValue(entity, navigationPropertyValue);
				}
			}
		}

		private static bool IsObjectValid(object e)
		{
			var validationContext = new ValidationContext(e);
			var validationErrors = new List<ValidationResult>();
			var validationResult = Validator.TryValidateObject(e, validationContext, validationErrors, validateAllProperties: true);
			return validationResult;
		}

		private IEnumerable<TEntity> LoadTableEntities<TEntity>() where TEntity:class
		{
			var table = typeof(TEntity);
			var columns = GetEntityColumnNames(table);
			var tableName = GetTableName(table);
			var fetchedRows = this.connection.FetchResultSet<TEntity>(tableName, columns).ToArray();
			return fetchedRows;
		}

		
		private string GetTableName(Type tableType)
		{
			var tableName = ((TableAttribute)Activator.CreateInstance(tableType, typeof(TableAttribute))).Name;
			if (tableName == null)
			{
				tableName = this.dbSetProperties[tableType].Name;
			}
			return tableName;
		}
		private Dictionary<Type,PropertyInfo> DiscoverDbSets()
		{
			var dbSets = this.GetType().GetProperties().Where(pi => pi.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)).ToDictionary(pi => pi.PropertyType.GetGenericArguments().First(), pi => pi);
			return dbSets;
		}

		private string[] GetEntityColumnNames(Type table)
		{
			var tableName = this.GetTableName(table);
			var dbColumns = this.connection.FetchColumnNames(tableName);
			var columns = table.GetProperties().Where(pi =>dbColumns.Contains(pi.Name)&&!pi.HasAttribute<NotMappedAttribute>()&&AllowedSqlTypes.Contains(pi.PropertyType)).Select(pi=>pi.Name).ToArray();
			return columns;
		}


	}
	// TODO: Create your DbContext class here.
}