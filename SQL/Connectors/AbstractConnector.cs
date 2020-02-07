using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public delegate void InitConnector(AbstractConnector connector);
    public delegate void OnException(Exception ex, string commandText);

    public abstract partial class AbstractConnector
    {
        protected readonly Stores.PasswordSettings _settings = null;
        public event InitConnector OnInit;
        public event OnException OnException;

        public bool IsInit { get; private set; } = false;

        public AbstractConnector(Stores.PasswordSettings settings)
        {
            _settings = settings;
        }

        public abstract DbConnection CreateConnection(Stores.PasswordSettings settings);

        public abstract string ConvertType(DbType type, Fields.AbstractField field);

        public abstract string FieldDefinition(Fields.AbstractField field);

        public virtual void InitException(Exception ex, string commandText)
        {
            if (OnException != null)
            {
                OnException?.Invoke(ex, commandText);
            }
            else
            {
                throw ex;
            }
        }

        public void DoInit()
        {
            if (!IsInit)
            {
                IsInit = true;
                OnInit?.Invoke(this);
                IsInit = false;
            }
        }

        private object EscapeValue(object item)
        {
            if(item is string)
            {
                return (item as string).Replace("'", "''");
            }
            return item;
        }

        public virtual DbCommand AddParameter(DbCommand command, string name, object value)
        {
            if (command.Parameters.Contains(name))
            {
                command.Parameters[name].Value = value ?? DBNull.Value;
            }
            else
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            return command;
        }

        public virtual string ConditionDefinition(Conditions.Condition condition, DbCommand command)
        {
            var result = new StringBuilder();
            if (condition != null)
            {
                if (condition.SubConditions != null && condition.SubConditions.Any())
                {
                    result.Append("(");
                    result.Append(ConditionDefinition(condition.SubConditions, command));
                    result.Append(")");
                }
                else if (!string.IsNullOrEmpty(condition.Name))
                {
                    result.Append(condition.Name);
                    switch (condition.Type)
                    {
                        case Conditions.ConditionType.IsNull:
                            if (condition.IsNot)
                            {
                                result.Append(" NOT ");
                            }
                            result.Append(" IS NULL");
                            break;
                        case Conditions.ConditionType.Less:
                            if (condition.IsNot)
                            {
                                result.Append(" >= ");
                            }
                            else
                            {
                                result.Append(" < ");
                            }
                            break;
                        case Conditions.ConditionType.Greather:
                            if (condition.IsNot)
                            {
                                result.Append(" <= ");
                            }
                            else
                            {
                                result.Append(" > ");
                            }
                            break;
                        case Conditions.ConditionType.LessAndEqual:
                            if (condition.IsNot)
                            {
                                result.Append(" > ");
                            }
                            else
                            {
                                result.Append(" <= ");
                            }
                            break;
                        case Conditions.ConditionType.GreatherAndEqual:
                            if (condition.IsNot)
                            {
                                result.Append(" < ");
                            }
                            else
                            {
                                result.Append(" >= ");
                            }
                            break;
                        case Conditions.ConditionType.Like:
                        case Conditions.ConditionType.EndsWith:
                        case Conditions.ConditionType.StartsWith:
                            if (condition.IsNot)
                            {
                                result.Append(" NOT ");
                            }
                            result.Append(" LIKE ");
                            break;
                        case Conditions.ConditionType.In:
                            if (condition.IsNot)
                            {
                                result.Append(" NOT ");
                            }
                            result.Append(" IN ");
                            break;
                        case Conditions.ConditionType.Equal:
                        default:
                            if (condition.IsNot)
                            {
                                result.Append(" <> ");
                            }
                            else
                            {
                                result.Append(" = ");
                            }
                            break;
                    }
                    if (condition.Type != Conditions.ConditionType.IsNull)
                    {
                        if (condition.Type == Conditions.ConditionType.In)
                        {
                            int i = 0;
                            result.Append("(");
                            foreach (var item in condition.Values)
                            {
                                if (i > 0)
                                {
                                    result.Append(", ");
                                }
                                if (!condition.IsField)
                                {
                                    var value = EscapeValue(item);
                                    var count = command.Parameters?.Count ?? 0;
                                    result.Append("@WHERE" + condition.Name.Replace(".", string.Empty) + i + "_" + count);
                                    AddParameter(command, "@WHERE" + condition.Name.Replace(".", string.Empty) + i + "_" + count, value);
                                }
                                i++;
                            }
                            result.Append(")");
                        }
                        else if (condition.Values != null)
                        {
                            var enumerator = condition.Values.GetEnumerator();
                            if (enumerator.MoveNext())
                            {
                                var first = enumerator.Current;
                                if (first != null)
                                {
                                    if (!condition.IsField)
                                    {
                                        object value = null;
                                        switch (condition.Type)
                                        {
                                            case Conditions.ConditionType.StartsWith:
                                                value = "%" + (first as string);
                                                break;
                                            case Conditions.ConditionType.Like:
                                                value = "%" + (first as string) + "%";
                                                break;
                                            case Conditions.ConditionType.EndsWith:
                                                value = (first as string) + "%";
                                                break;
                                            default:
                                                value = first;
                                                break;
                                        }
                                        value = EscapeValue(value);
                                        var count = command.Parameters?.Count ?? 0;
                                        result.Append("@WHERE" + condition.Name.Replace(".", string.Empty) + "_" + count);
                                        AddParameter(command, "@WHERE" + condition.Name.Replace(".", string.Empty) + "_" + count, value);
                                    }
                                    else
                                    {
                                        result.Append(first);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return result.ToString();
        }

        public virtual string ConditionDefinition(IEnumerable<Conditions.Condition> conditions, DbCommand command)
        {
            var result = new StringBuilder();
            if (conditions != null && conditions.Any())
            {
                int i = 0;
                foreach (var condition in conditions)
                {
                    if (i > 0)
                    {
                        if (condition.IsOr)
                        {
                            result.Append(" OR ");
                        }
                        else
                        {
                            result.Append(" AND ");
                        }
                    }
                    result.Append(ConditionDefinition(condition, command));
                    i++;
                }
            }
            return result.ToString();
        }

        public virtual DbCommand AddWhere(IEnumerable<Conditions.Condition> conditions, DbCommand command)
        {
            if (command != null && conditions != null && conditions.Any())
            {
                command.CommandText += " WHERE ";
                command.CommandText += ConditionDefinition(conditions, command);
            }
            return command;
        }

        public virtual void DoCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand, bool isLock = false)
        {
            if (!isLock)
            {
                RunCommand(createCommand, executeCommand);
            }
            else
            {
                lock (_settings)
                {
                    RunCommand(createCommand, executeCommand);
                }
            }
        }

        private void RunCommand(Action<DbCommand> createCommand, Action<DbCommand> executeCommand)
        {
            using (var db = CreateConnection(_settings))
            {
                db.Open();
                using (var transaction = db.BeginTransaction())
                {
                    string commandText = null;
                    try
                    {
                        using (var command = db.CreateCommand())
                        {
                            createCommand?.Invoke(command);
                            commandText = DataBase.GetGeneratedQuery(command);
                            executeCommand?.Invoke(command);
                        }
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        InitException(ex, commandText);
                    }
                }
            }
        }
    }
}
