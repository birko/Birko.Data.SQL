using Birko.Data.Filters;
using Birko.Data.Repositories;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Birko.Data.SQL.Extensions
{
    public static class IDataBaseRepositoryExtensions
    {
        public static TViewModel ReadOne<TRepository, TConnector, TViewModel, TModel>(this TRepository respository, IFilter<TModel>? filter = null, IDictionary<Expression<Func<TModel, object>>, bool> orderByExpr = null)
            where TRepository : AbstractViewModelRepository<TViewModel, TModel>, IDataBaseRepository<TConnector, TViewModel, TModel>
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
