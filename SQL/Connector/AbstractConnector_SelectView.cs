using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connector
{
    public abstract partial class AbstractConnector
    {
        public void SelectView(Type type, Action<object> readAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null)
        {
            SelectView(type, readAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields);
        }

        public void SelectView<T, P>(Type type, Action<object> readAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null)
        {
            SelectView(type, readAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetViewField(x.Key).GetSelectName(true), x => x.Value));
        }

        public void SelectView(Type type, Action<object> readAction, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            Select(DataBase.LoadView(type), (fields, reader) => {
                if (readAction != null)
                {
                    var data = Activator.CreateInstance(type, new object[0]);
                    DataBase.ReadView(reader, data);
                    readAction(data);
                }
            }, conditions, orderFields);
        }

        public void Select(Table.View view, Action<IDictionary<int, string>, DbDataReader> readAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null)
        {
            Select(view, readAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields);
        }

        public void Select<T, P>(Table.View view, Action<IDictionary<int, string>, DbDataReader> readAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null)
        {
            Select(view, readAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetViewField(x.Key).GetSelectName(true), x => x.Value));
        }

        public void Select(Table.View view, Action<IDictionary<int, string>, DbDataReader> readAction = null, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            if (view != null)
            {
                DoCommand((command) => {
                    command = CreateSelectCommand(command, view, conditions, orderFields);
                }, (command) =>
                {
                    var reader = command.ExecuteReader();
                    if (reader.HasRows)
                    {

                        bool isNext = reader.Read();
                        while (isNext)
                        {
                            readAction?.Invoke(view.GetSelectFields(), reader);
                            isNext = reader.Read();
                        }
                    }
                });
            }
        }
    }
}
