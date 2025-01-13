using Birko.Data.Filters;
using Birko.Data.Models;
using Birko.Data.Repositories;
using Birko.Data.ViewModels;
using FisData.Stock.API.Filters;
using FisData.Stock.Core.Repositories;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Birko.Data.DataBase.Extensions
{
    public static class IDataBaseRepositoryExtensions
    {
        public static TViewModel ReadOne<TRepository, TConnector, TViewModel, TModel>(this TRepository respository, IRepositoryFilter<TModel>? filter = null, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
            where TRepository : AbstractRepository<TViewModel, TModel>, IDataBaseRepository<TConnector, TViewModel, TModel>
            where TConnector : SQL.Connectors.AbstractConnector
            where TModel : Models.AbstractModel, Models.ILoadable<TViewModel>
            where TViewModel : Models.ILoadable<TModel>
        {
            if (respository.Connector != null)
            {
                foreach (TModel item in respository.Connector?.Select(typeof(TModel), filter?.Filter(), orderByExpr, 1, 0))
                {
                    return respository.LoadInstance(item);
                }
            }
            return default(TViewModel);
        }
    }
}
